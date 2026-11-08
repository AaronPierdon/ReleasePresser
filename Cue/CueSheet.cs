using System.Text;
using System.Text.RegularExpressions;

namespace Pressing.Core.Cue;

public sealed class CueTrack
{
    public int Number { get; set; }
    public string Type { get; set; } = "AUDIO";
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public string? SongWriter { get; set; }
    public string? Isrc { get; set; }
    /// <summary>INDEX number → position in CD frames (1/75 s) within its FILE.</summary>
    public SortedDictionary<int, long> Indexes { get; } = new();
    public long? PregapFrames { get; set; }
    public CueFile File { get; set; } = null!;
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
    public long? Index01 => Indexes.TryGetValue(1, out var v) ? v : null;
}

public sealed class CueFile
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "WAVE";
    public List<CueTrack> Tracks { get; } = new();
}

public sealed class CueSheet
{
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public string? Catalog { get; set; }
    public string? Genre { get; set; }
    public string? Date { get; set; }
    public string? DiscId { get; set; }
    public string? Comment { get; set; }
    public string? Label { get; set; }
    public int? DiscNumber { get; set; }
    public int? TotalDiscs { get; set; }
    public Dictionary<string, string> Rem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CueFile> Files { get; } = new();
    public string RawText { get; set; } = "";
    public string EncodingName { get; set; } = "";
    public string? SourcePath { get; set; }
    public bool Embedded { get; set; }

    public IEnumerable<CueTrack> AllTracks => Files.SelectMany(f => f.Tracks);
    public IEnumerable<CueTrack> AudioTracks => AllTracks.Where(t => t.IsAudio);

    /// <summary>A cue that describes several tracks inside one audio file (needs splitting).</summary>
    public bool IsImageCue => Files.Any(f => f.Tracks.Count(t => t.IsAudio) > 1);

    /// <summary>Quality used to choose between several cue files for the same image.</summary>
    public int QualityScore =>
        AudioTracks.Count() * 10
        + AudioTracks.Count(t => !string.IsNullOrWhiteSpace(t.Title)) * 5
        - (RawText.Contains('�') ? 100 : 0)
        + (EncodingName.StartsWith("utf", StringComparison.OrdinalIgnoreCase) ? 3 : 0);

    // ---------------------------------------------------------------- loading

    static CueSheet()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static CueSheet Load(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        var (text, enc) = Decode(bytes);
        var cue = Parse(text);
        cue.EncodingName = enc;
        cue.SourcePath = path;
        return cue;
    }

    /// <summary>
    /// Cue files come in every encoding imaginable. Order: BOM → strict UTF-8 → Shift-JIS (only if the text
    /// really looks Japanese) → Windows-1252.
    /// </summary>
    public static (string Text, string Encoding) Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), "utf-8-bom");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "utf-16le");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "utf-16be");

        try { return (new UTF8Encoding(false, true).GetString(b), "utf-8"); } catch (DecoderFallbackException) { }

        try
        {
            var sjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var t = sjis.GetString(b);
            int cjk = t.Count(c => c >= 0x3040 && c <= 0x9FFF);
            int halfKana = t.Count(c => c >= 0xFF61 && c <= 0xFF9F);
            int nonAscii = t.Count(c => c > 0x7F);
            if (nonAscii > 0 && cjk >= nonAscii * 0.6 && halfKana < cjk * 0.2) return (t, "shift_jis");
        }
        catch (DecoderFallbackException) { }

        return (Encoding.GetEncoding(1252).GetString(b), "windows-1252");
    }

    private static readonly Regex Tokenizer = new("\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled);

    public static CueSheet Parse(string text)
    {
        var cue = new CueSheet { RawText = text };
        CueFile? file = null;
        CueTrack? track = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            var toks = Tokenizer.Matches(line).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
            if (toks.Count == 0) continue;
            var cmd = toks[0].ToUpperInvariant();
            string Arg(int i) => i < toks.Count ? toks[i] : "";
            string Rest(int from) => string.Join(" ", toks.Skip(from));

            switch (cmd)
            {
                case "REM":
                    var key = Arg(1).ToUpperInvariant();
                    var val = Rest(2).Trim();
                    if (key.Length == 0) break;
                    if (track == null) cue.Rem[key] = val;
                    switch (key)
                    {
                        case "GENRE" when track == null: cue.Genre = val; break;
                        case "DATE" when track == null: cue.Date = val; break;
                        case "DISCID": cue.DiscId = val; break;
                        case "COMMENT" when track == null: cue.Comment = val; break;
                        case "LABEL" when track == null: cue.Label = val; break;
                        case "DISCNUMBER" when int.TryParse(val, out var dn): cue.DiscNumber = dn; break;
                        case "TOTALDISCS" when int.TryParse(val, out var td): cue.TotalDiscs = td; break;
                    }
                    break;
                case "CATALOG": cue.Catalog = Arg(1); break;
                case "TITLE":
                    if (track != null) track.Title = Rest(1); else cue.Title = Rest(1);
                    break;
                case "PERFORMER":
                    if (track != null) track.Performer = Rest(1); else cue.Performer = Rest(1);
                    break;
                case "SONGWRITER":
                    if (track != null) track.SongWriter = Rest(1);
                    break;
                case "FILE":
                    // FILE "name with spaces.flac" WAVE  — the type is the last token.
                    var type = toks.Count >= 3 ? toks[^1] : "WAVE";
                    var name = toks.Count >= 3 ? string.Join(" ", toks.Skip(1).Take(toks.Count - 2)) : Arg(1);
                    file = new CueFile { Name = name, Type = type };
                    cue.Files.Add(file);
                    // Keep the current track: in EAC "noncompliant" (gaps appended) cues a track is declared
                    // under the previous FILE with only INDEX 00, and its INDEX 01 follows the next FILE line.
                    break;
                case "TRACK":
                    if (file == null) { file = new CueFile { Name = "" }; cue.Files.Add(file); }
                    track = new CueTrack
                    {
                        Number = int.TryParse(Arg(1), out var n) ? n : file.Tracks.Count + 1,
                        Type = Arg(2).Length > 0 ? Arg(2) : "AUDIO",
                        File = file,
                    };
                    file.Tracks.Add(track);
                    break;
                case "INDEX" when track != null:
                    if (int.TryParse(Arg(1), out var idx) && TryParseMsf(Arg(2), out var frames))
                    {
                        if (track.File != file && file != null)
                        {
                            // INDEX after a new FILE line belongs to the pending track, in the NEW file.
                            if (idx == 0) break; // pregap at the start of the new file: nothing to keep
                            track.File.Tracks.Remove(track);
                            track.Indexes.Remove(0); // its INDEX 00 was a position in the previous file
                            track.File = file;
                            file.Tracks.Add(track);
                        }
                        track.Indexes[idx] = frames;
                    }
                    break;
                case "PREGAP" when track != null:
                    if (TryParseMsf(Arg(1), out var pg)) track.PregapFrames = pg;
                    break;
                case "ISRC" when track != null:
                    track.Isrc = Arg(1);
                    break;
            }
        }
        return cue;
    }

    public static bool TryParseMsf(string s, out long frames)
    {
        frames = 0;
        var p = s.Split(':');
        if (p.Length != 3) return false;
        if (!long.TryParse(p[0], out var m) || !long.TryParse(p[1], out var sec) || !long.TryParse(p[2], out var f)) return false;
        frames = (m * 60 + sec) * 75 + f;
        return true;
    }

    /// <summary>Converts CD frames to a sample position. Exact for 44.1/48/88.2/96/176.4/192 kHz.</summary>
    public static long FramesToSamples(long frames, int sampleRate) =>
        (long)Math.Round(frames * (double)sampleRate / 75.0);
}
