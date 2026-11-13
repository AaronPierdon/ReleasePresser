using System.Text.Json;
using System.Text.RegularExpressions;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

public sealed class MusicBrainzClient
{
    private const string Base = "https://musicbrainz.org/ws/2/";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1100);
    private const string Inc = "artist-credits+recordings+labels+release-groups+media";

    /// <summary>Exact lookup by disc ID; falls back to MusicBrainz's fuzzy TOC match.</summary>
    public async Task<List<Candidate>> ByDiscIdAsync(string discId, string? toc, CancellationToken ct)
    {
        var url = $"{Base}discid/{Uri.EscapeDataString(discId)}?inc={Inc}&fmt=json" + (toc != null ? $"&toc={toc}" : "");
        using var doc = await Http.GetJsonAsync(url, Interval, ct);
        if (doc == null) return new();
        var list = new List<Candidate>();
        foreach (var r in doc.RootElement.A("releases")) list.Add(Parse(r));
        return list;
    }

    public async Task<List<(string Id, int Score)>> SearchAsync(string album, string? artist, int? trackCount, CancellationToken ct)
    {
        var q = $"release:\"{Esc(album)}\"";
        if (!string.IsNullOrWhiteSpace(artist)) q += $" AND artist:\"{Esc(artist)}\"";
        var url = $"{Base}release/?query={Uri.EscapeDataString(q)}&fmt=json&limit=15";
        using var doc = await Http.GetJsonAsync(url, Interval, ct);
        var hits = new List<(string, int, int)>();
        if (doc == null) return new();
        foreach (var r in doc.RootElement.A("releases"))
        {
            var id = r.S("id");
            if (id == null) continue;
            int score = int.TryParse(r.S("score"), out var sc) ? sc : 0;
            int tc = int.TryParse(r.S("track-count"), out var t) ? t : 0;
            hits.Add((id, score, tc));
        }
        // Favour hits whose track count is close to ours.
        return hits
            .OrderByDescending(h => h.Item2 - (trackCount.HasValue ? Math.Abs(h.Item3 - trackCount.Value) * 4 : 0))
            .Select(h => (h.Item1, h.Item2)).ToList();
    }

    public async Task<Candidate?> GetReleaseAsync(string id, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync($"{Base}release/{id}?inc={Inc}&fmt=json", Interval, ct);
        return doc == null ? null : Parse(doc.RootElement);
    }

    private static string Esc(string s) => Regex.Replace(s, @"([+\-!(){}\[\]^""~*?:\\/]|&&|\|\|)", @"\$1");

    public static List<string> CreditNames(JsonElement credits) =>
        credits.ValueKind == JsonValueKind.Array
            ? credits.EnumerateArray().Select(c => c.S("name") ?? c.O("artist").S("name") ?? "").Where(n => n.Length > 0).ToList()
            : new();

    private static Candidate Parse(JsonElement r)
    {
        var rg = r.O("release-group");
        var c = new Candidate
        {
            Source = "MusicBrainz",
            Id = r.S("id") ?? "",
            Album = r.S("title") ?? "",
            AlbumArtists = CreditNames(r.O("artist-credit")),
            Year = TextUtil.ParseYear(r.S("date")),
            OriginalYear = TextUtil.ParseYear(rg.S("first-release-date")),
            Country = r.S("country"),
            Barcode = r.S("barcode"),
            Edition = string.Join(", ", new[] { r.S("disambiguation"), rg.S("primary-type") }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
        c.Url = $"https://musicbrainz.org/release/{c.Id}";
        var li = r.A("label-info").FirstOrDefault();
        if (li.ValueKind == JsonValueKind.Object)
        {
            c.Label = li.O("label").S("name");
            c.CatalogNumber = li.S("catalog-number");
        }
        var secondary = rg.A("secondary-types").Select(x => x.GetString()).ToList();
        c.IsCompilation = secondary.Contains("Compilation") || c.AlbumArtists.Any(a => a.Equals("Various Artists", StringComparison.OrdinalIgnoreCase));

        foreach (var m in r.A("media"))
        {
            var med = new CandidateMedium
            {
                Position = int.TryParse(m.S("position"), out var p) ? p : c.Media.Count + 1,
                Format = m.S("format"),
            };
            foreach (var t in m.A("tracks"))
            {
                var rec = t.O("recording");
                var artists = CreditNames(t.O("artist-credit"));
                if (artists.Count == 0) artists = CreditNames(rec.O("artist-credit"));
                if (artists.Count == 0) artists = c.AlbumArtists.ToList();
                double? len = double.TryParse(t.S("length") ?? rec.S("length"), out var ms) ? ms / 1000.0 : null;
                med.Tracks.Add(new CandidateTrack
                {
                    Disc = med.Position,
                    Position = int.TryParse(t.S("position"), out var tp) ? tp : med.Tracks.Count + 1,
                    Title = t.S("title") ?? rec.S("title") ?? "",
                    Artists = artists,
                    Length = len,
                });
            }
            // Skip data/video media (DVD bonus discs etc.) that have no audio tracks we could match.
            if (med.Tracks.Count > 0) c.Media.Add(med);
        }
        return c;
    }
}
