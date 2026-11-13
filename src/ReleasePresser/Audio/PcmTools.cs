using System.Buffers.Binary;
using System.Security.Cryptography;
using Pressing.Core.Util;

namespace Pressing.Core.Audio;

public static class PcmTools
{
    public static string PcmFormatFor(int bits) => bits switch
    {
        <= 8 => "u8",
        <= 16 => "s16le",
        <= 24 => "s24le",
        _ => "s32le",
    };

    /// <summary>
    /// Decodes each file to raw PCM at the given bit depth and feeds it, in order, into one SHA-256.
    /// Hashing the source image and the concatenated split tracks proves the split is bit-identical.
    /// </summary>
    public static async Task<(string Hash, long Bytes)> HashPcmAsync(string ffmpeg, IEnumerable<string> files, int bits, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var fmt = PcmFormatFor(bits);
        foreach (var f in files)
        {
            var r = await Proc.RunStreamAsync(ffmpeg, new[]
            {
                "-v", "error", "-nostdin", "-i", f, "-map", "0:a:0", "-f", fmt, "-acodec", "pcm_" + fmt, "-"
            }, async (s, token) =>
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await s.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    total += n;
                }
            }, ct);
            if (!r.Ok) throw new InvalidOperationException($"Decode failed for {Path.GetFileName(f)}: {Tail(r.StdErr)}");
        }
        return (Convert.ToHexString(hash.GetHashAndReset()), total);
    }

    /// <summary>
    /// Dynamic range in the style of the TT DR meter: 3-second blocks, RMS of the loudest 20% of blocks
    /// against the second-highest block peak, averaged over channels.
    /// </summary>
    public static async Task<double?> DynamicRangeAsync(string ffmpeg, string file, int channels, int sampleRate, CancellationToken ct)
    {
        if (channels <= 0 || sampleRate <= 0) return null;
        int blockFrames = sampleRate * 3;
        var rmsPerCh = Enumerable.Range(0, channels).Select(_ => new List<double>()).ToArray();
        var peakPerCh = Enumerable.Range(0, channels).Select(_ => new List<double>()).ToArray();
        var sumSq = new double[channels];
        var peak = new double[channels];
        int framesInBlock = 0;

        void Flush()
        {
            if (framesInBlock == 0) return;
            for (int c = 0; c < channels; c++)
            {
                rmsPerCh[c].Add(Math.Sqrt(2.0 * sumSq[c] / framesInBlock));
                peakPerCh[c].Add(peak[c]);
                sumSq[c] = 0; peak[c] = 0;
            }
            framesInBlock = 0;
        }

        var r = await Proc.RunStreamAsync(ffmpeg, new[]
        {
            "-v", "error", "-nostdin", "-i", file, "-map", "0:a:0", "-f", "f32le", "-acodec", "pcm_f32le", "-"
        }, async (s, token) =>
        {
            var buf = new byte[1 << 20];
            var carry = new byte[4 * channels];
            int carryLen = 0, ch = 0;
            int n;
            while ((n = await s.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
            {
                int i = 0;
                if (carryLen > 0)
                {
                    while (carryLen < 4 && i < n) carry[carryLen++] = buf[i++];
                    if (carryLen == 4) { Accumulate(BinaryPrimitives.ReadSingleLittleEndian(carry)); carryLen = 0; }
                }
                for (; i + 4 <= n; i += 4) Accumulate(BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(i, 4)));
                while (i < n) carry[carryLen++] = buf[i++];
            }

            void Accumulate(float v)
            {
                double a = Math.Abs(v);
                sumSq[ch] += a * a;
                if (a > peak[ch]) peak[ch] = a;
                if (++ch == channels)
                {
                    ch = 0;
                    if (++framesInBlock == blockFrames) Flush();
                }
            }
        }, ct);
        if (!r.Ok) return null;
        Flush();

        var drs = new List<double>();
        for (int c = 0; c < channels; c++)
        {
            var rms = rmsPerCh[c].OrderByDescending(x => x).ToList();
            var peaks = peakPerCh[c].OrderByDescending(x => x).ToList();
            if (rms.Count == 0) continue;
            int top = Math.Max(1, (int)Math.Round(rms.Count * 0.2));
            double rmsTop = Math.Sqrt(rms.Take(top).Average(x => x * x));
            double pk = peaks.Count > 1 ? peaks[1] : peaks[0];
            if (rmsTop <= 0 || pk <= 0) continue;
            drs.Add(20 * Math.Log10(pk / rmsTop));
        }
        return drs.Count == 0 ? null : drs.Average();
    }

    private static string Tail(string s) => s.Length > 300 ? s[^300..] : s;
}
