using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pressing.Core.Util;

public static partial class TextUtil
{
    // ---------------- File-system names ----------------

    public static string SanitizeName(string name, string colonReplacement, string illegalReplacement)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_";
        var s = name.Replace(":", colonReplacement);
        s = Regex.Replace(s, "[\\\\/*?\"<>|]", illegalReplacement);
        s = Regex.Replace(s, @"[\x00-\x1F]", "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.TrimEnd('.', ' ');
        // Windows reserved device names
        if (Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase)) s = "_" + s;
        if (s.Length > 150) s = s[..150].TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Fills a template like "{track} - {title}" and tidies empty brackets left by missing values.</summary>
    public static string FillTemplate(string template, IReadOnlyDictionary<string, string?> values)
    {
        var s = Regex.Replace(template, @"\{(\w+)\}", m =>
            values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v ?? "" : m.Value);
        s = Regex.Replace(s, @"\(\s*\)|\[\s*\]", "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = Regex.Replace(s, @"^[\s\-–_.]+|[\s\-–_]+$", "");
        return s.Trim();
    }

    // ---------------- Titles ----------------

    private static readonly Regex DiscTrackNo = new(@"^\s*\d{1,2}[-.]\d{1,3}\s*[-._]?\s+(?=\S)", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\s*(?:(?:cd|disc|disk)\s*\d+\s*[-._]?\s*)?(\d{1,3})(\s*[-._)\]]\s*|\s+)(?=\S)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Cleans a title taken from tags or a filename: strips track-number prefixes, an "Artist - " prefix,
    /// underscores and file extensions. Official titles from a database should NOT go through this.
    /// A leading number is only removed when it looks like a track number (zero-padded, followed by a
    /// separator, or equal to <paramref name="trackNo"/>), so "99 Problems" and "1979" survive.
    /// </summary>
    public static string CleanTitle(string? raw, IEnumerable<string>? artistsToStrip = null, int? trackNo = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        s = Regex.Replace(s, @"\.(flac|mp3|ape|wav|m4a|ogg|opus|wv)$", "", RegexOptions.IgnoreCase);
        if (!s.Contains(' ') && s.Contains('_')) s = s.Replace('_', ' ');
        s = DiscTrackNo.Replace(s, "");
        var m = LeadingNumber.Match(s);
        if (m.Success)
        {
            var num = m.Groups[1].Value;
            bool padded = num.Length >= 2 && num[0] == '0';
            bool separator = m.Groups[2].Value.Trim().Length > 0;
            bool equalsTrack = trackNo.HasValue && int.Parse(num) == trackNo.Value;
            if (padded || separator || equalsTrack) s = s[m.Length..];
        }

        if (artistsToStrip != null)
        {
            foreach (var a in artistsToStrip.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                var prefix = Regex.Escape(a.Trim());
                s = Regex.Replace(s, $"^{prefix}\\s*[-–]\\s+", "", RegexOptions.IgnoreCase);
            }
        }
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '–', '.');
        return s.Length == 0 ? raw.Trim() : s;
    }

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    { "a", "an", "the", "and", "but", "or", "nor", "for", "of", "on", "in", "at", "to", "by", "vs", "vs.", "de", "la", "le", "feat.", "ft." };

    /// <summary>Title-cases text only when it is ALL CAPS or all lowercase; otherwise trusts the existing casing.</summary>
    public static string SmartCase(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return s;
        bool allUpper = letters.All(char.IsUpper), allLower = letters.All(char.IsLower);
        if (!allUpper && !allLower) return s;
        if (allUpper && letters.Count <= 4) return s; // acronyms like "ABBA", "U2"

        var words = s.ToLowerInvariant().Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            bool edge = i == 0 || i == words.Length - 1 || words[i - 1].EndsWith(':') || words[i - 1].EndsWith('(');
            if (!edge && SmallWords.Contains(w)) continue;
            int k = 0;
            while (k < w.Length && !char.IsLetterOrDigit(w[k])) k++;
            if (k < w.Length) words[i] = w[..k] + char.ToUpperInvariant(w[k]) + w[(k + 1)..];
        }
        return string.Join(' ', words);
    }

    // ---------------- Artists ----------------

    private static readonly Regex FeatSplit = new(@"\s+(?:feat\.?|ft\.?|featuring|with)\s+|\s*;\s*|\s+/\s+|\s+x\s+|\s+vs\.?\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits an artist string on unambiguous delimiters (feat., ft., ;, /, x, vs). Deliberately does NOT split
    /// on "&amp;" or "," because band names use them ("Simon &amp; Garfunkel", "Crosby, Stills, Nash &amp; Young").
    /// </summary>
    public static List<string> SplitArtists(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new();
        var parts = FeatSplit.Split(s).Select(p => p.Trim().Trim('(', ')', '[', ']').Trim()).Where(p => p.Length > 0);
        return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string StripDiscogsSuffix(string name) => Regex.Replace(name, @"\s\(\d+\)$", "").Trim().TrimEnd('*');

    // ---------------- Matching helpers ----------------

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == '&') sb.Append(" and ");
            else sb.Append(' ');
        }
        var r = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        r = Regex.Replace(r, @"^the ", "");
        return r;
    }

    private static readonly Regex EditionWords = new(
        @"[\(\[][^\)\]]*(remaster|deluxe|expanded|edition|anniversary|bonus|special|reissue|version|mono|stereo|hi-?res|24|bit|khz|flac|web|vinyl|japan|sacd|mfsl|mofi|limited|collector|digipak|import|ltd|explicit|clean)[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Album title reduced to its core, for dupe grouping ("OK Computer (Collector's Edition)" → "ok computer").</summary>
    public static string AlbumCore(string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return "";
        var s = EditionWords.Replace(album, " ");
        s = Regex.Replace(s, @"[\(\[]\s*(19|20)\d{2}\s*[\)\]]", " ");
        s = Regex.Replace(s, @"\s[-–]\s.*(remaster|edition|version).*$", " ", RegexOptions.IgnoreCase);
        return Normalize(s);
    }

    private static readonly Regex FeatClause = new(@"[\(\[]?\s*\b(feat\.?|ft\.?|featuring)\s+[^\)\]]*[\)\]]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemasterNote = new(@"\s*([\(\[][^\)\]]*(remaster|mono|stereo|bonus track|single version|album version|explicit|clean)[^\)\]]*[\)\]]|\s[-–]\s.*(remaster|mono|stereo|single version|album version).*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Title reduced to what identifies the recording: drops "feat. X" and remaster/mono/album-version notes,
    /// but KEEPS remix / rework / edit / live / extended / piano-version text, because those are different recordings.
    /// </summary>
    public static string RecordingTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = FeatClause.Replace(title, " ");
        s = RemasterNote.Replace(s, " ");
        return Normalize(s);
    }

    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (x.Length == 0 && y.Length == 0) return 1;
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        var d = Levenshtein(x, y);
        var sim = 1.0 - (double)d / Math.Max(x.Length, y.Length);
        // A title contained in the other (e.g. "song" vs "song remastered 2011") is a strong match.
        if (x.Length >= 4 && y.Length >= 4 && (x.Contains(y) || y.Contains(x))) sim = Math.Max(sim, 0.85);
        return sim;
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ---------------- Folder-name parsing ----------------

    /// <summary>
    /// Pulls a probable album title and year out of a messy download folder name, e.g.
    /// "Alanis Morissette - 1995 - Jagged Little Pill [FLAC 24-96] {WEB}" → ("Jagged Little Pill", 1995).
    /// </summary>
    public static (string Album, int? Year) ParseFolderName(string folder, string? artist)
    {
        var s = folder;
        int? year = null;
        var ym = Regex.Match(s, @"(?<!\d)(19[5-9]\d|20[0-4]\d)(?!\d)");
        if (ym.Success) year = int.Parse(ym.Value);

        s = Regex.Replace(s, @"\[[^\]]*\]|\{[^\}]*\}", " ");
        s = Regex.Replace(s, @"\((?:[^)]*(?:flac|mp3|web|cd|vinyl|24|16|bit|khz|320|v0|lossless|rip|log|cue)[^)]*)\)", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[\(]?(?<!\d)(19[5-9]\d|20[0-4]\d)(?!\d)[\)]?", " ");
        // Catalog numbers in brackets: (0091005EXT), (XTRAV1CDS), (44K 79424), (MLCD-0801) — capitals and digits only.
        s = Regex.Replace(s, @"\(\s*(?=[^)]*\d)[A-Z0-9][A-Z0-9 .#\-]{2,}\)", " ");
        s = s.Replace('_', ' ');
        if (!string.IsNullOrWhiteSpace(artist))
            s = Regex.Replace(s, "^\\s*" + Regex.Escape(artist) + "\\s*[-–]", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(flac|mp3|web|cd|vinyl|lossless|24bit|16bit|hi-?res)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*[-–]\s*[-–]\s*", " - ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '–', '.', '_');
        // "Artist - Album" where we don't know the artist: keep the part after the last " - "
        if (string.IsNullOrWhiteSpace(artist) && s.Contains(" - ")) s = s[(s.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..];
        return (s, year);
    }

    public static int? ParseYear(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"(?<!\d)(1[89]\d{2}|20\d{2})(?!\d)");
        return m.Success ? int.Parse(m.Value) : null;
    }

    public static int? ParseLeadingInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"^\s*(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static readonly Regex DiscFolderPattern = new(@"^\s*(?:cd|disc|disk|dvd|side)\s*[-_.]?\s*(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
