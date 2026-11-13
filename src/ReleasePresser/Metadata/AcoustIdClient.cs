using System.Globalization;
using System.Text.Json;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

/// <summary>Identifies tracks from the audio itself (Chromaprint fingerprint) and votes on the release they belong to.</summary>
public sealed class AcoustIdClient
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(350);
    private readonly string _key;
    private readonly string _fpcalc;

    public AcoustIdClient(string key, string fpcalc)
    {
        _key = key;
        _fpcalc = fpcalc;
    }

    /// <summary>Returns MusicBrainz release IDs ordered by how many of the given files point at them.</summary>
    public async Task<List<string>> VoteReleasesAsync(IEnumerable<string> files, int expectedTracks, CancellationToken ct)
    {
        var votes = new Dictionary<string, int>();
        foreach (var f in files.Take(6))
        {
            var fp = await Proc.RunAsync(_fpcalc, new[] { "-json", f }, ct);
            if (!fp.Ok) continue;
            string? print; double dur;
            try
            {
                using var j = JsonDocument.Parse(fp.StdOut);
                print = j.RootElement.S("fingerprint");
                dur = double.Parse(j.RootElement.S("duration") ?? "0", CultureInfo.InvariantCulture);
            }
            catch { continue; }
            if (string.IsNullOrEmpty(print)) continue;

            var url = $"https://api.acoustid.org/v2/lookup?client={_key}&meta=recordings+releases&duration={(int)dur}&fingerprint={print}";
            using var doc = await Http.GetJsonAsync(url, Interval, ct);
            if (doc == null) continue;
            var seen = new HashSet<string>();
            foreach (var res in doc.RootElement.A("results"))
            {
                if (double.TryParse(res.S("score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) && sc < 0.6) continue;
                foreach (var rec in res.A("recordings"))
                foreach (var rel in rec.A("releases"))
                {
                    var id = rel.S("id");
                    if (id == null || !seen.Add(id)) continue;
                    int tc = int.TryParse(rel.S("track_count"), out var t) ? t : 0;
                    votes[id] = votes.GetValueOrDefault(id) + (tc == expectedTracks ? 3 : 1);
                }
            }
        }
        return votes.OrderByDescending(v => v.Value).Take(5).Select(v => v.Key).ToList();
    }
}
