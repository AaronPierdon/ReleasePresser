using Pressing.Core.Models;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

public sealed class MatchResult
{
    public Candidate Candidate { get; init; } = null!;
    public double Score { get; set; }
    /// <summary>Local track → candidate track (null = not on this release, i.e. a bonus track).</summary>
    public Dictionary<TrackFile, CandidateTrack?> Map { get; } = new();
    public int Matched => Map.Values.Count(v => v != null);
    public int Unmatched => Map.Values.Count(v => v == null);
}

/// <summary>Scores how well a candidate release fits the local files, using durations first and titles second.</summary>
public static class Matcher
{
    public static List<TrackFile> LocalOrder(Release r) =>
        r.Discs.OrderBy(d => d.Number)
            .SelectMany(d => d.Tracks.OrderBy(t => t.Number > 0 ? t.Number : int.MaxValue).ThenBy(t => t.OriginalFileName, StringComparer.OrdinalIgnoreCase))
            .ToList();

    public static MatchResult Score(Release rel, Candidate c, string? albumHint, string? artistHint)
    {
        var local = LocalOrder(rel);
        var remote = c.Media.OrderBy(m => m.Position).SelectMany(m => m.Tracks.OrderBy(t => t.Position)).ToList();
        var res = new MatchResult { Candidate = c };
        if (local.Count == 0 || remote.Count == 0) return res;

        // AI candidates name the file for each track.
        if (remote.Any(t => t.FileName != null))
        {
            foreach (var l in local)
            {
                var hit = remote.FirstOrDefault(t => t.FileName != null && string.Equals(Path.GetFileName(t.FileName), Path.GetFileName(l.Path), StringComparison.OrdinalIgnoreCase))
                          ?? remote.FirstOrDefault(t => t.FileName != null && string.Equals(Path.GetFileNameWithoutExtension(t.FileName), Path.GetFileNameWithoutExtension(l.Path), StringComparison.OrdinalIgnoreCase));
                res.Map[l] = hit;
            }
            double coverage = res.Matched / (double)local.Count;
            res.Score = c.Score * (0.4 + 0.6 * coverage);
            res.Score *= ArtistFactor(c, artistHint);
            return res;
        }

        var sims = new double[local.Count, remote.Count];
        for (int i = 0; i < local.Count; i++)
            for (int j = 0; j < remote.Count; j++)
                sims[i, j] = PairSim(local[i], remote[j]);

        double total = 0;
        if (local.Count == remote.Count)
        {
            // Same length: map by position, which also handles untitled or garbage-titled files.
            for (int i = 0; i < local.Count; i++)
            {
                res.Map[local[i]] = sims[i, i] >= 0.35 ? remote[i] : null;
                total += sims[i, i];
            }
            res.Score = total / local.Count;
        }
        else
        {
            // Different lengths (bonus tracks or missing tracks): greedy best pairs.
            var pairs = new List<(int i, int j, double s)>();
            for (int i = 0; i < local.Count; i++)
                for (int j = 0; j < remote.Count; j++)
                    if (sims[i, j] >= 0.6) pairs.Add((i, j, sims[i, j] - Math.Abs(i - j) * 0.002));
            var usedL = new HashSet<int>(); var usedR = new HashSet<int>();
            foreach (var p in pairs.OrderByDescending(p => p.s))
            {
                if (usedL.Contains(p.i) || usedR.Contains(p.j)) continue;
                usedL.Add(p.i); usedR.Add(p.j);
                res.Map[local[p.i]] = remote[p.j];
                total += sims[p.i, p.j];
            }
            foreach (var l in local) res.Map.TryAdd(l, null);
            double avg = usedL.Count == 0 ? 0 : total / usedL.Count;
            double coverage = usedL.Count / (double)local.Count;
            double missing = remote.Count > local.Count ? Math.Pow(local.Count / (double)remote.Count, 0.5) : 1;
            res.Score = avg * Math.Pow(coverage, 0.7) * missing;
        }

        // Album / artist agreement nudges the score.
        if (!string.IsNullOrWhiteSpace(albumHint))
        {
            var a = Math.Max(TextUtil.Similarity(albumHint, c.Album), TextUtil.Similarity(TextUtil.AlbumCore(albumHint), TextUtil.AlbumCore(c.Album)));
            res.Score *= 0.85 + 0.15 * a;
        }
        res.Score *= ArtistFactor(c, artistHint);
        if (local.Select(l => l.Disc).Distinct().Count() != c.Media.Count) res.Score *= 0.95;
        return res;
    }

    /// <summary>
    /// A release by a different artist must not win just because track lengths line up
    /// (one-track singles match almost anything by duration). Wrong artist → heavy penalty.
    /// </summary>
    private static double ArtistFactor(Candidate c, string? artistHint)
    {
        if (string.IsNullOrWhiteSpace(artistHint) || c.IsCompilation) return 1;
        var names = c.AlbumArtists.Concat(c.AllTracks.SelectMany(t => t.Artists)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (names.Count == 0) return 0.9;
        double best = names.Max(n => Math.Max(TextUtil.Similarity(artistHint, n),
            Pipeline.PipelineRunner.ArtistMatches(n, artistHint) || Pipeline.PipelineRunner.ArtistMatches(artistHint, n) ? 1 : 0));
        return best >= 0.85 ? 1 : best >= 0.6 ? 0.85 : 0.3;
    }

    /// <summary>Same recording: (nearly) the same title and the same length. A remix or rework has a different title or length.</summary>
    public static bool SameRecording(TrackFile a, TrackFile b)
    {
        var ta = TextUtil.RecordingTitle(a.After.Title ?? a.Before.Title ?? Path.GetFileNameWithoutExtension(a.Path));
        var tb = TextUtil.RecordingTitle(b.After.Title ?? b.Before.Title ?? Path.GetFileNameWithoutExtension(b.Path));
        if (ta.Length == 0 || tb.Length == 0) return false;
        if (ta != tb && 1.0 - (double)TextUtil.Levenshtein(ta, tb) / Math.Max(ta.Length, tb.Length) < 0.9) return false;
        if (a.Duration <= 0 || b.Duration <= 0) return false;
        if (Math.Abs(a.Duration - b.Duration) > Math.Max(4, Math.Max(a.Duration, b.Duration) * 0.03)) return false;
        // Same title and length but no artist in common ("Intro" by two different acts on a label) isn't the same recording.
        var aa = Artists(a); var ab = Artists(b);
        if (aa.Count > 0 && ab.Count > 0 && !aa.Overlaps(ab)) return false;
        return true;
    }

    private static HashSet<string> Artists(TrackFile t)
    {
        var set = new HashSet<string>();
        foreach (var src in new[] { t.After.Artist, t.Before.Artist, t.After.AlbumArtist, t.Before.AlbumArtist })
            foreach (var n in TextUtil.SplitArtists(src))
            {
                var k = TextUtil.Normalize(n);
                if (k.Length > 0 && k != "various artists" && k != "va") set.Add(k);
            }
        return set;
    }

    private static double PairSim(TrackFile l, CandidateTrack r)
    {
        double? dur = null;
        if (l.Duration > 0 && r.Length is > 0)
        {
            var d = Math.Abs(l.Duration - r.Length.Value);
            dur = d <= 2 ? 1 : Math.Max(0, 1 - (d - 2) / 10.0);
        }
        double? title = null;
        var lt = TextUtil.CleanTitle(l.Before.Title ?? Path.GetFileNameWithoutExtension(l.Path), null, l.Number);
        if (!string.IsNullOrWhiteSpace(lt) && !System.Text.RegularExpressions.Regex.IsMatch(lt, @"^(track|piste|titel)\s*\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            title = TextUtil.Similarity(lt, r.Title);

        if (dur.HasValue && title.HasValue)
        {
            if (title.Value >= 0.95 && dur.Value >= 0.3) return Math.Max(0.6 * dur.Value + 0.4 * title.Value, 0.9);
            return 0.6 * dur.Value + 0.4 * title.Value;
        }
        if (dur.HasValue) return dur.Value * 0.95;
        if (title.HasValue) return title.Value * 0.9;
        return 0.5;
    }
}
