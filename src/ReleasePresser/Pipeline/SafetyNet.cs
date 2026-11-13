using Pressing.Core.Metadata;
using Pressing.Core.Models;

namespace Pressing.Core.Pipeline;

/// <summary>
/// The "nothing lost" guarantee. Before anything is removed, every recording in every folder marked for removal
/// must also exist in a folder that stays — at least as good (a lossless track needs a lossless copy).
/// This is checked across ALL planned removals together, so chains like "single → in album → album culled as a
/// dupe" can't make a recording disappear. Anything that fails is protected (kept) and shown to you.
/// </summary>
public static class SafetyNet
{
    /// <summary>What the current choices would remove, before protection.</summary>
    public static HashSet<Release> RequestedRemovals(RunReport report, PressingSettings s)
    {
        var set = new HashSet<Release>();
        foreach (var g in report.DupeGroups.Where(g => !g.Resolved && g.KeepReleaseId != null))
        {
            var winner = report.Releases.FirstOrDefault(r => r.Id == g.KeepReleaseId);
            if (winner == null) continue;
            foreach (var v in g.Versions.Where(v => v.ReleaseId != g.KeepReleaseId))
            {
                var r = report.Releases.FirstOrDefault(x => x.Id == v.ReleaseId);
                if (r == null || r.State == ReleaseState.Culled) continue;
                // Same rule DupeResolver.Apply uses: versions with tracks the winner lacks aren't removed.
                bool extra = r.AllTracks.Any(t => !winner.AllTracks.Any(w => Matcher.SameRecording(t, w)));
                if (s.NeverCullVersionWithExtraTracks && extra) continue;
                set.Add(r);
            }
        }
        foreach (var r in report.Releases.Where(r => r.RemoveAsRedundant && r.State != ReleaseState.Culled)) set.Add(r);
        return set;
    }

    /// <summary>
    /// Works out which requested removals are safe. Unsafe ones get <see cref="Release.Protected"/> set (Apply skips them).
    /// Returns the preview audit: how many recordings stay, how many live on elsewhere, which removals were refused.
    /// </summary>
    public static (HashSet<Release> Removals, SafetyAudit Audit) Check(RunReport report, PressingSettings s)
    {
        foreach (var r in report.Releases) r.Protected = null;
        var removal = RequestedRemovals(report, s);
        var audit = new SafetyAudit();

        // Keep the fewest folders needed: each round protects the one removal holding the most recordings that
        // nothing kept has, then looks again (keeping it may make the others safe).
        while (true)
        {
            var kept = report.Releases.Where(r => !removal.Contains(r) && r.State != ReleaseState.Culled).ToList();
            var worst = removal
                .Select(r => (Rel: r, Orphans: r.AllTracks.Where(t => !kept.Any(k => k.AllTracks.Any(x => Covers(x, t)))).ToList()))
                .Where(x => x.Orphans.Count > 0)
                .OrderByDescending(x => x.Orphans.Count).ThenByDescending(x => x.Rel.AllTracks.Count())
                .FirstOrDefault();
            if (worst.Rel == null) break;
            removal.Remove(worst.Rel);
            var o = worst.Orphans;
            var names = string.Join(", ", o.Take(3).Select(Title)) + (o.Count > 3 ? $" +{o.Count - 3}" : "");
            worst.Rel.Protected = $"Kept for safety: {o.Count} recording{(o.Count == 1 ? "" : "s")} not in any folder you're keeping" +
                                  (o.Any(t => t.Lossless) ? " (in lossless)" : "") + $" — {names}";
        }
        foreach (var r in report.Releases.Where(r => r.Protected != null)) audit.Blocked.Add($"{r.OriginalName}: {r.Protected}");

        var all = report.Releases.Where(r => r.State != ReleaseState.Culled).ToList();
        audit.Recordings = all.Sum(r => r.AllTracks.Count());
        audit.CoveredElsewhere = removal.Sum(r => r.AllTracks.Count());
        audit.InPlace = audit.Recordings - audit.CoveredElsewhere;
        return (removal, audit);
    }

    /// <summary>After Apply: is every recording either still on disk, or in a removed folder with a copy in a kept one?</summary>
    public static SafetyAudit Verify(RunReport report)
    {
        var audit = new SafetyAudit();
        var kept = report.Releases.Where(r => r.State != ReleaseState.Culled).ToList();
        foreach (var r in report.Releases)
        {
            if (r.State == ReleaseState.Culled)
            {
                foreach (var t in r.AllTracks)
                {
                    audit.Recordings++;
                    if (kept.Any(k => k.AllTracks.Any(x => Covers(x, t) && File.Exists(x.Path)))) audit.CoveredElsewhere++;
                    else audit.Lost.Add($"{r.OriginalName} / {Title(t)} — removed, no copy found");
                }
                continue;
            }
            // A release whose split you didn't apply: its music is the original image, the split tracks were deleted again.
            var undoneSplit = !r.Include && r.WasSplit ? new HashSet<string>(r.SplitOutputs, StringComparer.OrdinalIgnoreCase) : null;
            foreach (var t in r.AllTracks)
            {
                audit.Recordings++;
                if (undoneSplit != null && undoneSplit.Contains(t.Path))
                {
                    if (r.PendingImageRemovals.Any(File.Exists)) audit.InPlace++;
                    else audit.Lost.Add($"{r.OriginalName} / {Title(t)} — image missing");
                    continue;
                }
                if (File.Exists(t.Path)) audit.InPlace++;
                else audit.Lost.Add($"{r.OriginalName} / {Title(t)} — file not found at {t.Path}");
            }
        }
        foreach (var r in report.Releases.Where(r => r.Protected != null)) audit.Blocked.Add($"{r.OriginalName}: {r.Protected}");
        return audit;
    }

    /// <summary>Keeper <paramref name="k"/> covers <paramref name="t"/>: same recording, and lossless if t is lossless.</summary>
    private static bool Covers(TrackFile k, TrackFile t) => Matcher.SameRecording(t, k) && (!t.Lossless || k.Lossless);

    private static string Title(TrackFile t) => t.After.Title ?? t.Before.Title ?? Path.GetFileNameWithoutExtension(t.Path);
}
