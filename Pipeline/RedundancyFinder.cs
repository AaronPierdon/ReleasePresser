using Pressing.Core.Metadata;
using Pressing.Core.Models;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

/// <summary>
/// Finds releases whose every recording also exists on other releases in the folder — singles whose
/// track is on the album, compilations made entirely of tracks you already have. It only marks them;
/// you choose what to remove on the review screen.
/// </summary>
public static class RedundancyFinder
{
    public static void Mark(List<Release> releases)
    {
        var active = releases.Where(r => r.State != ReleaseState.Culled && r.State != ReleaseState.Failed && !r.Untouched && r.AllTracks.Any()).ToList();

        // Index tracks by the start of their normalized title so we don't compare everything with everything.
        var index = new Dictionary<string, List<(Release Rel, TrackFile Track)>>();
        static string Key(TrackFile t)
        {
            var n = TextUtil.Normalize(t.After.Title ?? t.Before.Title ?? "");
            return n.Length <= 4 ? n : n[..4];
        }
        foreach (var r in active)
            foreach (var t in r.AllTracks)
            {
                var k = Key(t);
                if (!index.TryGetValue(k, out var l)) index[k] = l = new();
                l.Add((r, t));
            }

        var fullyIn = new Dictionary<Release, List<Release>>();
        foreach (var r in active)
        {
            r.ContainedIn.Clear();
            r.Redundant = false;
            var hosts = new Dictionary<Release, int>();
            bool all = true;
            foreach (var t in r.AllTracks)
            {
                var found = index.TryGetValue(Key(t), out var l)
                    ? l.Where(x => x.Rel != r && Matcher.SameRecording(t, x.Track)).Select(x => x.Rel).Distinct().ToList()
                    : new List<Release>();
                foreach (var h in found) hosts[h] = hosts.GetValueOrDefault(h) + 1;
                // Only releases at least as big count as "already having" this track, so an album is never
                // called redundant because its tracks also came out as singles.
                if (!found.Any(h => h.AllTracks.Count() >= r.AllTracks.Count())) all = false;
            }
            if (hosts.Count == 0) continue;
            r.ContainedIn = hosts.OrderByDescending(h => h.Value).Select(h => new Containment
            {
                ReleaseId = h.Key.Id,
                Name = Path.GetFileName(h.Key.Path),
                Tracks = h.Value,
            }).ToList();
            if (all) fullyIn[r] = hosts.Keys.ToList();
        }

        foreach (var (r, hosts) in fullyIn)
        {
            // Versions of the same album are the duplicate checker's job, not this one.
            if (r.DupeGroup != null) continue;

            // Two releases that contain each other completely (same recordings, different names):
            // only the less complete / worse-tagged one is redundant.
            var mutual = hosts.Where(h => fullyIn.TryGetValue(h, out var back) && back.Contains(r)).ToList();
            if (mutual.Any(h => Better(h, r))) { r.Redundant = true; }
            else if (mutual.Count == 0) r.Redundant = true;

            if (r.Redundant)
            {
                var where = string.Join(", ", r.ContainedIn.Take(3).Select(c => $"\"{c.Name}\""));
                r.AddFlag(Severity.Info, Stages.Dupes, $"Every track here is already on {where}{(r.ContainedIn.Count > 3 ? $" and {r.ContainedIn.Count - 3} more" : "")}.");
            }
        }
    }

    private static bool Better(Release a, Release b) =>
        a.AllTracks.Count() != b.AllTracks.Count()
            ? a.AllTracks.Count() > b.AllTracks.Count()
            : DupeResolver.Richness(a) >= DupeResolver.Richness(b);
}
