using Pressing.Core.Audio;
using Pressing.Core.Metadata;
using Pressing.Core.Models;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

/// <summary>
/// Finds versions of the same album and recommends which to keep. Analysis never removes anything:
/// it only sets <see cref="DupeGroup.RecommendedReleaseId"/>. <see cref="Apply"/> acts on your final choice.
///
/// Two releases count as versions of one album only when they share most of their actual recordings
/// (same title AND same length), so remixes, reworks and edits with the same name are never "duplicates".
/// </summary>
public sealed class DupeResolver
{
    private readonly PressingSettings _s;
    private readonly Toolset _tools;
    private readonly FileOps? _ops;
    private readonly GeminiClient? _ai;
    private readonly Action<string> _log;

    public DupeResolver(PressingSettings s, Toolset tools, FileOps? ops, GeminiClient? ai, Action<string> log)
    {
        _s = s; _tools = tools; _ops = ops; _ai = ai; _log = log;
    }

    public static string GroupKey(Release r) =>
        TextUtil.Normalize(r.IsCompilation ? "va" : r.AlbumArtist) + "|" + TextUtil.AlbumCore(r.Album);

    /// <summary>Share of the smaller release's tracks that are the same recordings on the other.</summary>
    public static double Overlap(Release a, Release b)
    {
        var ta = a.AllTracks.ToList(); var tb = b.AllTracks.ToList();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var (small, big) = ta.Count <= tb.Count ? (ta, tb) : (tb, ta);
        int hits = small.Count(x => big.Any(y => Matcher.SameRecording(x, y)));
        return hits / (double)small.Count;
    }

    public List<DupeGroup> FindGroups(IEnumerable<Release> releases)
    {
        var groups = new List<DupeGroup>();
        var candidates = releases.Where(r => r.State != ReleaseState.Culled && r.State != ReleaseState.Failed && !r.Untouched && !string.IsNullOrWhiteSpace(r.Album));
        foreach (var byTitle in candidates.GroupBy(GroupKey).Where(g => g.Count() > 1 && g.Key.Split('|')[1].Length > 0))
        {
            // Within a same-title bucket, link releases that really share recordings (≥ 60% of the smaller one).
            var list = byTitle.ToList();
            var parent = Enumerable.Range(0, list.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (Overlap(list[i], list[j]) >= 0.6) parent[Find(i)] = Find(j);

            foreach (var cluster in list.Select((r, i) => (r, root: Find(i))).GroupBy(x => x.root).Where(c => c.Count() > 1))
            {
                var rels = cluster.Select(x => x.r).ToList();
                var key = byTitle.Key + "#" + cluster.Key;
                foreach (var r in rels) r.DupeGroup = key;
                groups.Add(new DupeGroup
                {
                    Key = key,
                    Title = $"{rels[0].AlbumArtist} — {rels[0].Album}",
                    Versions = rels.Select(r => new DupeVersion
                    {
                        ReleaseId = r.Id, Name = Path.GetFileName(r.Path), Path = r.Path,
                        TrackCount = r.AllTracks.Count(), Edition = r.Match?.Edition, Format = r.FormatSummary, Keep = true,
                    }).ToList(),
                });
            }
        }
        return groups;
    }

    /// <summary>Works out which version to recommend. Changes nothing on disk.</summary>
    public async Task RecommendAsync(DupeGroup group, List<Release> all, CancellationToken ct)
    {
        var rels = group.Versions.Select(v => all.First(r => r.Id == v.ReleaseId)).ToList();

        if (rels.All(r => SameAudio(r, rels[0])))
        {
            Recommend(group, rels.OrderByDescending(Richness).First(), "identical",
                "Same audio in more than one folder; keeping the copy with the best tags.");
            return;
        }

        foreach (var r in rels)
        {
            if (r.DynamicRange == null)
                using (var t = RunLog.Time("dr", $"measure dynamic range: {Path.GetFileName(r.Path)}"))
                {
                    r.DynamicRange = await MeasureAsync(r, ct);
                    t.Result = r.DynamicRange is { } d ? $"DR{d:0.0}" : "couldn't measure";
                }
            group.Versions.First(v => v.ReleaseId == r.Id).DynamicRange = r.DynamicRange;
        }

        var pool = rels.Where(r => r.IsLossless).ToList();
        if (pool.Count == 0) pool = rels;

        // One version holds every recording of the others plus more → it's the complete one.
        var complete = pool.Where(r => rels.All(o => o == r || o.AllTracks.All(t => r.AllTracks.Any(x => Metadata.Matcher.SameRecording(t, x)))))
                           .Where(r => rels.Any(o => o != r && o.AllTracks.Count() < r.AllTracks.Count())).ToList();
        if (complete.Count == 1)
        {
            Recommend(group, complete[0], "complete", $"\"{Path.GetFileName(complete[0].Path)}\" contains every track of the other version(s) and more.");
            return;
        }

        if (pool.Count == 1)
        {
            Recommend(group, pool[0], "quality", "Only lossless version.");
            return;
        }

        var byDr = pool.OrderByDescending(r => r.DynamicRange ?? -1).ToList();
        double top = byDr[0].DynamicRange ?? -1, second = byDr[1].DynamicRange ?? -1;
        if (top >= 0 && top - second >= _s.DrMarginDb)
        {
            Recommend(group, byDr[0], "dr", $"Highest dynamic range: DR{top:0.0} vs DR{second:0.0}.");
            return;
        }

        if (_s.AiTiebreaker && _ai != null)
        {
            var close = byDr.Where(r => (r.DynamicRange ?? -1) >= top - _s.DrMarginDb).ToList();
            (int Index, double Confidence, string Reason)? pick;
            string? aiError;
            await _ai.Gate.WaitAsync(ct);
            try { pick = await _ai.PickBestMasteringAsync(close[0].AlbumArtist ?? "", close[0].Album ?? "", close.Select(Describe).ToList(), ct); aiError = _ai.LastError; }
            finally { _ai.Gate.Release(); }
            if (pick is { } p && p.Confidence >= 0.55)
            {
                Recommend(group, close[p.Index], "ai", $"AI ({p.Confidence:0.00}): {p.Reason}");
                return;
            }
            group.Reason = pick == null ? $"DR too close and AI gave no answer ({aiError})." : $"DR too close; AI unsure: {pick.Value.Reason}";
        }
        else group.Reason = $"DR too close to call (DR{top:0.0} vs DR{second:0.0}).";

        if (_s.AutoKeepBestWhenUndecided)
        {
            var best = pool.OrderByDescending(r => r.MaxBitDepth).ThenByDescending(r => r.MaxSampleRate)
                .ThenByDescending(r => r.Match?.Confidence ?? 0).ThenByDescending(r => r.AllTracks.Count()).First();
            Recommend(group, best, "fallback", group.Reason + " Suggesting the highest resolution with the richest metadata.");
            return;
        }

        group.DecidedBy = "undecided";
        group.KeepReleaseId = null;
        foreach (var r in rels) r.DupeDecision = "You decide which version to keep.";
    }

    private void Recommend(DupeGroup g, Release keep, string by, string reason)
    {
        g.DecidedBy = by;
        g.Reason = reason;
        g.RecommendedReleaseId = keep.Id;
        // Exact copies go automatically. Different editions only when you say so (or when you've turned that on).
        g.KeepReleaseId = by == "identical" || !_s.AutoRemoveOnlyIdentical ? keep.Id : null;
        if (g.KeepReleaseId == null) g.Reason += " Nothing is removed unless you pick a version.";
    }

    /// <summary>
    /// Removes every version except <see cref="DupeGroup.KeepReleaseId"/> (nothing if it's null).
    /// A version with recordings the kept one lacks is never removed.
    /// </summary>
    public void Apply(DupeGroup group, List<Release> all)
    {
        if (group.KeepReleaseId == null)
        {
            foreach (var v in group.Versions) v.Keep = true;
            group.Resolved = true;
            return;
        }
        var winner = all.First(r => r.Id == group.KeepReleaseId);
        foreach (var v in group.Versions)
        {
            var r = all.First(x => x.Id == v.ReleaseId);
            if (r.Protected != null)
            {
                v.Keep = true;
                r.DupeDecision = r.Protected;
                continue;
            }
            if (r == winner)
            {
                v.Keep = true;
                r.DupeDecision = $"Kept ({group.DecidedBy}): {group.Reason}";
                continue;
            }
            var extra = r.AllTracks.Where(t => !winner.AllTracks.Any(w => Matcher.SameRecording(t, w))).ToList();
            if (_s.NeverCullVersionWithExtraTracks && extra.Count > 0)
            {
                v.Keep = true;
                r.DupeDecision = $"Kept: has {extra.Count} recording(s) the chosen version lacks.";
                r.AddFlag(Severity.Info, Stages.Dupes, $"Not removed: {extra.Count} track(s) not on the kept version ({string.Join(", ", extra.Take(3).Select(t => t.After.Title))}).");
                continue;
            }
            Cull(v, r, winner, group.DecidedBy);
        }
        group.Resolved = true;
    }

    private void Cull(DupeVersion v, Release r, Release winner, string why)
    {
        try
        {
            using (RunLog.Time("remove", $"remove duplicate {Path.GetFileName(r.Path)}", logStart: false))
                _ops!.Remove(r.Path);
            r.State = ReleaseState.Culled;
            r.DupeDecision = $"Removed in favour of \"{Path.GetFileName(winner.Path)}\" ({why}).";
            v.Keep = false;
            _log($"  removed {Path.GetFileName(r.Path)} ({why})");
        }
        catch (Exception e)
        {
            v.Keep = true;
            r.AddFlag(Severity.Warning, Stages.Dupes, $"Couldn't remove duplicate: {e.Message}");
        }
    }

    /// <summary>Best-tagged copy first: confident match, fewer problems, no " (2)" style suffix.</summary>
    public static double Richness(Release r) =>
        (r.Match?.Confidence ?? 0) * 10
        - r.Flags.Count(f => f.Severity != Severity.Info)
        - (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(r.Path), @"\(\d\)$|copy", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 3 : 0)
        - Path.GetFileName(r.Path).Length * 0.01;

    /// <summary>Same track count, same format, and every track within 0.15 s of its counterpart.</summary>
    public static bool SameAudio(Release a, Release b)
    {
        var ta = Matcher.LocalOrder(a);
        var tb = Matcher.LocalOrder(b);
        if (ta.Count == 0 || ta.Count != tb.Count) return false;
        for (int i = 0; i < ta.Count; i++)
        {
            var x = ta[i]; var y = tb[i];
            if (!string.Equals(x.Codec, y.Codec, StringComparison.OrdinalIgnoreCase)) return false;
            if (x.SampleRate != y.SampleRate || x.BitDepth != y.BitDepth) return false;
            if (!x.Lossless && Math.Abs(x.BitrateKbps - y.BitrateKbps) > Math.Max(8, x.BitrateKbps * 0.05)) return false;
            if (Math.Abs(x.Duration - y.Duration) > 0.15) return false;
        }
        return true;
    }

    private static string Describe(Release r) =>
        $"{Path.GetFileName(r.Path)} | album \"{r.Album}\" | year {r.Year?.ToString() ?? "?"} | label {r.Label ?? "?"} {r.CatalogNumber} | edition {r.Match?.Edition ?? "?"} | " +
        $"{r.FormatSummary} | {r.AllTracks.Count()} tracks | measured DR{(r.DynamicRange is { } d ? d.ToString("0.0") : "?")}";

    /// <summary>Album DR = mean of track DRs; samples up to 8 tracks spread across the album to keep it quick.</summary>
    public async Task<double?> MeasureAsync(Release r, CancellationToken ct)
    {
        if (_tools.Ffmpeg == null || _tools.Ffprobe == null) return null;
        var tracks = r.AllTracks.ToList();
        if (tracks.Count == 0) return null;
        var step = Math.Max(1, tracks.Count / 8.0);
        var sample = Enumerable.Range(0, Math.Min(8, tracks.Count)).Select(i => tracks[(int)(i * step)]).Distinct().ToList();
        var values = new List<double>();
        foreach (var t in sample)
        {
            var info = await AudioProbe.ProbeAsync(_tools.Ffprobe, t.Path, ct);
            if (info == null) continue;
            var dr = await PcmTools.DynamicRangeAsync(_tools.Ffmpeg, t.Path, info.Channels, info.SampleRate, ct);
            if (dr.HasValue) { t.DynamicRange = Math.Round(dr.Value, 1); values.Add(dr.Value); }
        }
        return values.Count == 0 ? null : Math.Round(values.Average(), 1);
    }
}
