using System.Text.Json;
using System.Text.RegularExpressions;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

public sealed class DiscogsClient
{
    private const string Base = "https://api.discogs.com/";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1100);
    private readonly string _token;

    public DiscogsClient(string token) => _token = token;

    private Task<JsonDocument?> Get(string url, CancellationToken ct) =>
        Http.GetJsonAsync(url, Interval, ct, r => r.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={_token}"));

    public async Task<List<string>> SearchAsync(string album, string? artist, CancellationToken ct)
    {
        var url = $"{Base}database/search?type=release&per_page=10&release_title={Uri.EscapeDataString(album)}";
        if (!string.IsNullOrWhiteSpace(artist)) url += $"&artist={Uri.EscapeDataString(artist)}";
        using var doc = await Get(url, ct);
        if (doc == null) return new();
        return doc.RootElement.A("results").Select(r => r.S("id")).Where(id => id != null).Select(id => id!).ToList();
    }

    public async Task<List<string>> SearchByCatalogAsync(string catno, CancellationToken ct)
    {
        using var doc = await Get($"{Base}database/search?type=release&per_page=5&catno={Uri.EscapeDataString(catno)}", ct);
        return doc == null ? new() : doc.RootElement.A("results").Select(r => r.S("id")).Where(id => id != null).Select(id => id!).ToList();
    }

    public async Task<Candidate?> GetReleaseAsync(string id, bool wantMasterYear, CancellationToken ct)
    {
        using var doc = await Get($"{Base}releases/{id}", ct);
        if (doc == null) return null;
        var r = doc.RootElement;
        var c = new Candidate
        {
            Source = "Discogs",
            Id = id,
            Url = r.S("uri") ?? $"https://www.discogs.com/release/{id}",
            Album = r.S("title") ?? "",
            AlbumArtists = Names(r.A("artists")),
            Year = int.TryParse(r.S("year"), out var y) && y > 0 ? y : TextUtil.ParseYear(r.S("released")),
            Country = r.S("country"),
        };
        var lab = r.A("labels").FirstOrDefault();
        if (lab.ValueKind == JsonValueKind.Object)
        {
            c.Label = TextUtil.StripDiscogsSuffix(lab.S("name") ?? "");
            c.CatalogNumber = lab.S("catno");
        }
        c.Barcode = r.A("identifiers").Where(i => (i.S("type") ?? "").Equals("Barcode", StringComparison.OrdinalIgnoreCase))
            .Select(i => Regex.Replace(i.S("value") ?? "", @"\D", "")).FirstOrDefault(v => v.Length >= 10);
        var fmts = r.A("formats").SelectMany(f => f.A("descriptions").Select(d => d.GetString())).Where(d => d != null).ToList();
        c.Edition = string.Join(", ", fmts.Where(d => d is not ("Album" or "LP")).Take(4));
        c.IsCompilation = fmts.Contains("Compilation") || c.AlbumArtists.Any(a => a.StartsWith("Various", StringComparison.OrdinalIgnoreCase));

        // Tracklist: skip headings, flatten index tracks, work out disc numbers from positions like "2-05" or "CD2-5".
        var tracks = new List<CandidateTrack>();
        int seq = 0;
        foreach (var t in r.A("tracklist"))
        {
            var type = t.S("type_") ?? "track";
            if (type == "heading") continue;
            var pos = t.S("position") ?? "";
            int disc = 1, num;
            var m = Regex.Match(pos, @"^(?:CD|DVD|Disc)?\s*(\d+)[-.](\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { disc = int.Parse(m.Groups[1].Value); num = int.Parse(m.Groups[2].Value); }
            else num = ++seq;
            var artists = Names(t.A("artists"));
            tracks.Add(new CandidateTrack
            {
                Disc = disc,
                Position = num,
                Title = t.S("title") ?? "",
                Artists = artists.Count > 0 ? artists : c.AlbumArtists.ToList(),
                Length = ParseDuration(t.S("duration")),
            });
        }
        // Renumber per disc in case positions were letters (vinyl sides).
        foreach (var g in tracks.GroupBy(t => t.Disc).OrderBy(g => g.Key))
        {
            var med = new CandidateMedium { Position = g.Key };
            int i = 0;
            foreach (var t in g) { t.Position = ++i; med.Tracks.Add(t); }
            c.Media.Add(med);
        }

        if (wantMasterYear && int.TryParse(r.S("master_id"), out var master) && master > 0)
        {
            using var md = await Get($"{Base}masters/{master}", ct);
            if (md != null && int.TryParse(md.RootElement.S("year"), out var my) && my > 0) c.OriginalYear = my;
        }
        return c;
    }

    private static List<string> Names(IEnumerable<JsonElement> artists) =>
        artists.Select(a => TextUtil.StripDiscogsSuffix(a.S("name") ?? "")).Where(n => n.Length > 0).Distinct().ToList();

    private static double? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(':');
        try
        {
            return p.Length switch
            {
                2 => int.Parse(p[0]) * 60 + int.Parse(p[1]),
                3 => int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]),
                _ => null
            };
        }
        catch { return null; }
    }
}
