using System.IO.Compression;
using System.Text;
using Pressing.Core.Util;

namespace Pressing.Core.Audio;

/// <summary>
/// Some WavPack images (e.g. scene releases "…(EAC-WV,CUE-LOG-COV.embedded)…zip.wv") have a ZIP archive with the
/// cue, log and cover glued onto the FRONT of the file. Players that scan for the WavPack header still play them, but
/// ffmpeg/ffprobe expect the header at byte 0 and report "Invalid data found". This finds where the real WavPack
/// audio starts, reads the embedded cue/log out of the ZIP part, and makes a clean temporary copy of the audio
/// for splitting. The original file is never modified.
/// </summary>
public static class WavPackFix
{
    private static readonly byte[] Magic = "wvpk"u8.ToArray();

    /// <summary>True when the file is a .wv that does NOT start with the WavPack header.</summary>
    public static bool NeedsCheck(string path)
    {
        if (!path.EndsWith(".wv", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[4];
            return f.Read(head, 0, 4) == 4 && !head.AsSpan().SequenceEqual(Magic);
        }
        catch { return false; }
    }

    /// <summary>Byte offset of the first valid WavPack block, searching the first 512 MB. Null when there is none.</summary>
    public static long? FindAudioStart(string path, long searchLimit = 512L << 20)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[4 << 20];
            long pos = 0;
            int carry = 0;
            while (pos < Math.Min(f.Length, searchLimit))
            {
                int read = f.Read(buf, carry, buf.Length - carry);
                if (read <= 0) break;
                int len = carry + read;
                for (int i = 0; i + 10 <= len; i++)
                {
                    if (buf[i] != (byte)'w' || buf[i + 1] != (byte)'v' || buf[i + 2] != (byte)'p' || buf[i + 3] != (byte)'k') continue;
                    uint ckSize = BitConverter.ToUInt32(buf, i + 4);
                    ushort version = BitConverter.ToUInt16(buf, i + 8);
                    if (ckSize is > 24 and < (1u << 24) && version is >= 0x402 and <= 0x410)
                        return pos - carry + i;
                }
                // keep the last 9 bytes in case a header straddles the buffer edge
                carry = Math.Min(9, len);
                Array.Copy(buf, len - carry, buf, 0, carry);
                pos += read;
            }
        }
        catch { }
        return null;
    }

    public sealed record Embedded(string? Cue, string? Log, string? CoverName, byte[]? Cover, List<string> Entries);

    /// <summary>Reads the ZIP glued to the front (or end) of the file. Returns null if there's no readable archive.</summary>
    public static Embedded? ReadEmbeddedZip(string path, long audioStart)
    {
        foreach (var (from, length) in new[] { (0L, audioStart), (0L, -1L) })
        {
            if (length == 0) continue;
            try
            {
                using var f = File.OpenRead(path);
                Stream s = length > 0 ? new SubStream(f, from, length) : f;
                using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
                string? cue = null, log = null, coverName = null;
                byte[]? cover = null;
                var names = new List<string>();
                foreach (var e in zip.Entries)
                {
                    names.Add(e.FullName);
                    var n = e.FullName.ToLowerInvariant();
                    if (n.EndsWith(".cue") && cue == null) cue = ReadText(e);
                    else if (n.EndsWith(".log") && log == null) log = ReadText(e);
                    else if ((n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".png")) && e.Length < 30 << 20)
                    {
                        bool better = cover == null || System.Text.RegularExpressions.Regex.IsMatch(n, "front|cover|folder");
                        if (better)
                        {
                            using var es = e.Open();
                            using var ms = new MemoryStream();
                            es.CopyTo(ms);
                            cover = ms.ToArray();
                            coverName = Path.GetFileName(e.FullName);
                        }
                    }
                }
                return new Embedded(cue, log, coverName, cover, names);
            }
            catch { /* not a zip at this position */ }
        }
        return null;
    }

    private static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Cue.CueSheet.Decode(ms.ToArray()).Text;
    }

    /// <summary>
    /// Copies the audio part (from <paramref name="audioStart"/> to the end) to a temporary file with the SAME name,
    /// so cue sheets that refer to the original name still match. Returns the temp path.
    /// </summary>
    public static string MakeCleanCopy(string path, long audioStart)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Pressing", "wv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(path));
        using (var src = File.OpenRead(path))
        using (var dst = File.Create(dest))
        {
            src.Seek(audioStart, SeekOrigin.Begin);
            src.CopyTo(dst, 4 << 20);
        }
        return dest;
    }

    public static void DeleteCleanCopy(string tempPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(tempPath)!;
            if (dir.Contains(Path.Combine(Path.GetTempPath(), "Pressing"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch { }
    }

    /// <summary>A read-only window onto part of another stream (the ZIP in front of the audio).</summary>
    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start, _length;
        private long _pos;

        public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _length); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length) return 0;
            _inner.Seek(_start + _pos, SeekOrigin.Begin);
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _length - _pos));
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
