using Pressing.Core.Audio;
using Pressing.Core.Cue;
using Pressing.Core.Models;
using Pressing.Core.Tags;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

/// <summary>One cue + the image file(s) it describes, waiting to be split.</summary>
public sealed class SplitJob
{
    public CueSheet Cue { get; init; } = null!;
    /// <summary>Cue FILE entry → resolved audio file on disk.</summary>
    public Dictionary<CueFile, string> Images { get; } = new();
    /// <summary>Other files that become redundant once the split is verified (other cue copies, other-format images).</summary>
    public List<string> Redundant { get; } = new();
    public string Folder { get; init; } = "";
    /// <summary>Where the tracks go. Differs from Folder when one folder holds several discs' images.</summary>
    public string OutputFolder { get; set; } = "";
    public int DiscNumber { get; set; } = 1;
    public string ReleaseId { get; set; } = "";
}

public sealed class ScanResult
{
    public List<Release> Releases { get; } = new();
    public List<SplitJob> SplitJobs { get; } = new();
    /// <summary>Cue / image files that are junk because the release is already split.</summary>
    public Dictionary<string, List<string>> CleanupOnly { get; } = new();
    /// <summary>Temporary clean copies of WavPack images that had an archive glued to the front (delete after splitting).</summary>
    public List<string> CleanCopies { get; } = new();
    /// <summary>Audio files sitting directly in the artist/label folder. They're never touched.</summary>
    public List<string> LooseAudio { get; } = new();
}

/// <summary>
/// Finds absolute releases anywhere under the root (any depth): a release is a folder that directly holds
/// audio, or a folder whose CD1/Disc 2/… subfolders hold audio. Then decides which ones need splitting.
/// </summary>
public sealed class Scanner
{
    private readonly Toolset _tools;
    private readonly Action<string> _log;

    public Scanner(Toolset tools, Action<string> log)
    {
        _tools = tools;
        _log = log;
    }

    /// <summary>A folder (e.g. the Pressed output) whose contents are finished and must not be scanned again.</summary>
    public string? ExcludeDir { get; set; }
    /// <summary>More folders to leave out completely: older output folders and folders you chose to skip.</summary>
    public List<string> ExcludeDirs { get; } = new();

    public async Task<ScanResult> ScanAsync(string root, CancellationToken ct)
    {
        var result = new ScanResult();
        var audioDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => !Path.GetFileName(d).StartsWith("_pressing", StringComparison.OrdinalIgnoreCase))
            .Where(d => !ExcludeDirs.Append(ExcludeDir).Any(x => x != null && (FileOps.PathsEqual(d, x) || d.StartsWith(x.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            .Prepend(root)
            .Where(d => Directory.EnumerateFiles(d).Any(f => Formats.IsAudio(f) || f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Group disc subfolders under their release folder.
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in audioDirs)
        {
            var name = Path.GetFileName(d);
            var parent = Path.GetDirectoryName(d)!;
            bool isDisc = TextUtil.DiscFolderPattern.IsMatch(name) && !FileOps.PathsEqual(d, root);
            var key = isDisc ? parent : d;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(d);
        }

        foreach (var (releaseDir, groupDirs) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var dirs = groupDirs;
            ct.ThrowIfCancellationRequested();
            if (FileOps.PathsEqual(releaseDir, root))
            {
                // Loose audio directly in the artist/label folder is left alone: it isn't in a release folder,
                // so there's no folder to name, and renaming loose files is not what anyone expects.
                foreach (var f in Directory.EnumerateFiles(root).Where(Formats.IsAudio)) result.LooseAudio.Add(f);
                if (dirs.Count == 1) continue;
                dirs = dirs.Where(d => !FileOps.PathsEqual(d, root)).ToList();
                if (dirs.Count == 0) continue;
            }

            var rel = new Release
            {
                Path = releaseDir,
                OriginalPath = releaseDir,
                OriginalName = Path.GetFileName(releaseDir),
                Group = GroupOf(root, releaseDir),
                FolderCatalog = CatalogFromFolder(Path.GetFileName(releaseDir)),
            };
            if (FileOps.PathsEqual(releaseDir, root))
                rel.AddFlag(Severity.Warning, Stages.Scan, "Audio files sit directly in the artist/label folder, not in a release folder.");

            var discDirs = dirs.OrderBy(d => DiscNumberOf(d) ?? 999).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            int n = 1;
            foreach (var d in discDirs)
            {
                bool sub = !FileOps.PathsEqual(d, releaseDir);
                var disc = new DiscFolder
                {
                    Path = d,
                    OriginalName = Path.GetFileName(d),
                    IsSubfolder = sub,
                    Number = sub ? DiscNumberOf(d) ?? n : 1,
                };
                n = disc.Number + 1;
                rel.Discs.Add(disc);
                await AnalyzeFolderAsync(rel, disc, result, ct);
            }

            // Context docs: logs / nfo / txt next to the release
            foreach (var f in Directory.EnumerateFiles(releaseDir, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
                         .Take(4))
            {
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    var (text, _) = CueSheet.Decode(bytes.Length > 40_000 ? bytes[..40_000] : bytes);
                    rel.ContextDocs.Add($"--- {Path.GetFileName(f)} ---\n{text}");
                }
                catch { }
            }

            result.Releases.Add(rel);
        }
        return result;
    }

    /// <summary>"…\Label\13 Hippie Dance\[HD01] X" → "13 Hippie Dance". "" when the release sits directly in the root.</summary>
    public static string GroupOf(string root, string releaseDir)
    {
        var parent = Path.GetDirectoryName(releaseDir.TrimEnd(Path.DirectorySeparatorChar));
        if (parent == null || FileOps.PathsEqual(parent, root) || !parent.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
        var rel = Path.GetRelativePath(root, parent);
        return rel == "." || rel.StartsWith("..") ? "" : rel;
    }

    private static readonly System.Text.RegularExpressions.Regex LeadingCatalog = new(@"^\s*\[([^\]]{2,40})\]");

    /// <summary>"[HIPPIE DANCE 01] Pachanga Boys - …" → "HIPPIE DANCE 01".</summary>
    public static string? CatalogFromFolder(string name)
    {
        var m = LeadingCatalog.Match(name);
        if (!m.Success) return null;
        var c = m.Groups[1].Value.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(c, @"\d") ? c : null; // "[FLAC]" etc. aren't catalog numbers
    }

    private static int? DiscNumberOf(string dir)
    {
        var m = TextUtil.DiscFolderPattern.Match(Path.GetFileName(dir));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private async Task AnalyzeFolderAsync(Release rel, DiscFolder disc, ScanResult result, CancellationToken ct)
    {
        var dir = disc.Path;
        var files = Directory.GetFiles(dir);
        var audio = files.Where(Formats.IsAudio).ToList();
        var cuePaths = files.Where(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();

        // WavPack images with a ZIP (cue/log/cover) glued to the front: split from a clean copy of the audio part.
        var cleanToOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embeddedZipCues = new List<CueSheet>();
        for (int i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            if (!WavPackFix.NeedsCheck(a)) continue;
            long? start;
            using (var t = RunLog.Time("scan", $"look inside {Path.GetFileName(a)} (doesn't start with a WavPack header)"))
            {
                start = WavPackFix.FindAudioStart(a);
                t.Result = start is > 0 ? $"WavPack audio starts at byte {start:N0}" : "no WavPack audio found — really unreadable";
            }
            if (start is not > 0) continue;
            var emb = WavPackFix.ReadEmbeddedZip(a, start.Value);
            if (emb != null)
            {
                RunLog.Write("scan", $"{Path.GetFileName(a)}: embedded archive holds {string.Join(", ", emb.Entries.Take(8))}{(emb.Entries.Count > 8 ? $" +{emb.Entries.Count - 8}" : "")}");
                if (emb.Log != null) rel.ContextDocs.Add($"--- log inside {Path.GetFileName(a)} ---\n{emb.Log}");
                if (emb.Cue != null)
                {
                    var cs = CueSheet.Parse(emb.Cue);
                    cs.Embedded = true;
                    cs.SourcePath = a;
                    cs.EncodingName = "zip inside .wv";
                    if (cs.Files.Count == 1) cs.Files[0].Name = Path.GetFileName(a);
                    embeddedZipCues.Add(cs);
                    rel.ContextDocs.Add($"--- cue inside {Path.GetFileName(a)} ---\n{emb.Cue}");
                }
            }
            string clean;
            using (RunLog.Time("scan", $"make a clean copy of the audio in {Path.GetFileName(a)}"))
                clean = WavPackFix.MakeCleanCopy(a, start.Value);
            cleanToOriginal[clean] = a;
            result.CleanCopies.Add(clean);
            audio[i] = clean;
            rel.SplitNotes.Add($"{Path.GetFileName(a)} had an archive ({(emb == null ? "unreadable" : string.Join(", ", emb.Entries.Select(Path.GetExtension).Distinct()))}) glued to the front of the audio; split from a clean copy, original untouched until you apply.");
        }

        var cues = new List<CueSheet>();
        foreach (var c in cuePaths)
        {
            try
            {
                var cs = CueSheet.Load(c);
                cues.Add(cs);
                rel.ContextDocs.Add($"--- {Path.GetFileName(c)} ({cs.EncodingName}) ---\n{cs.RawText}");
            }
            catch (Exception e) { rel.AddFlag(Severity.Warning, Stages.Scan, $"Unreadable cue {Path.GetFileName(c)}: {e.Message}"); }
        }

        // Embedded cue sheets inside single big lossless files.
        if (cues.Count == 0 && audio.Count is >= 1 and <= 3)
        {
            foreach (var a in audio.Where(a => Formats.LosslessExtensions.Contains(Path.GetExtension(a))))
            {
                var emb = TagIO.ReadEmbeddedCue(a);
                if (string.IsNullOrWhiteSpace(emb)) continue;
                var cs = CueSheet.Parse(emb);
                cs.Embedded = true;
                cs.SourcePath = a;
                cs.EncodingName = "embedded";
                if (cs.Files.Count == 1) cs.Files[0].Name = Path.GetFileName(a);
                if (cs.IsImageCue) { cues.Add(cs); rel.ContextDocs.Add($"--- embedded cue in {Path.GetFileName(a)} ---\n{emb}"); }
            }
        }

        // The cue from inside the .wv only counts when the folder has no cue of its own.
        if (cues.Count == 0) cues.AddRange(embeddedZipCues);

        var imageCues = cues.Where(c => c.IsImageCue).ToList();
        var junkCues = cues.Where(c => !c.IsImageCue).Select(c => c.SourcePath!).Where(p => p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();

        // Resolve each image cue's FILE entries to actual audio files.
        var resolved = new List<(CueSheet Cue, Dictionary<CueFile, List<string>> Map)>();
        foreach (var cue in imageCues)
        {
            var map = new Dictionary<CueFile, List<string>>();
            foreach (var cf in cue.Files.Where(f => f.Tracks.Any(t => t.IsAudio)))
            {
                var matches = ResolveImage(cf.Name, audio, cue.SourcePath, imageCues.Count == 1 && cue.Files.Count == 1);
                if (matches.Count > 0) map[cf] = matches;
            }
            resolved.Add((cue, map));
        }

        var usedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<SplitJob>();

        // Several cues for the same image → keep the best one, the rest are redundant.
        foreach (var grp in resolved.Where(r => r.Map.Count > 0)
                     .GroupBy(r => string.Join("|", r.Map.Values.SelectMany(v => v).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))))
        {
            var best = grp.OrderByDescending(r => r.Cue.QualityScore).First();
            var images = best.Map.Values.SelectMany(v => v).ToList();
            var otherTracks = audio.Except(images, StringComparer.OrdinalIgnoreCase)
                .Where(a => !resolved.Any(r => r.Map.Values.Any(v => v.Contains(a, StringComparer.OrdinalIgnoreCase)))).ToList();

            int cueTrackCount = best.Cue.AudioTracks.Count();
            if (otherTracks.Count >= cueTrackCount && cueTrackCount > 0)
            {
                // Already split; the image is a leftover. Only remove it if the split tracks cover it.
                var imgDur = await TotalDurationAsync(images.Take(1), ct);
                var trkDur = await TotalDurationAsync(otherTracks, ct);
                if (imgDur > 0 && Math.Abs(imgDur - trkDur) <= Math.Max(3, imgDur * 0.01))
                {
                    AddCleanup(result, rel.Id, images.Concat(grp.Select(g => g.Cue.SourcePath!)).Where(p => p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || images.Contains(p)));
                    rel.SplitNotes.Add($"{disc.OriginalName}: already split; image + cue marked for cleanup.");
                }
                else
                {
                    rel.AddFlag(Severity.Warning, Stages.Scan, $"{disc.OriginalName}: has both an image and {otherTracks.Count} tracks, but their lengths differ ({imgDur:0}s vs {trkDur:0}s). Left alone.");
                }
                foreach (var i in images) usedImages.Add(i);
                continue;
            }

            // Safety net: if a "image" is shorter than where the cue's tracks start, the cue doesn't describe it
            // (typically a per-track cue next to tracks that are already split).
            bool fits = true;
            foreach (var (cf, candidates) in best.Map)
            {
                var lastStart = cf.Tracks.Where(t => t.IsAudio && t.Index01.HasValue).Select(t => t.Index01!.Value / 75.0).DefaultIfEmpty(0).Max();
                var dur = await TotalDurationAsync(candidates.Take(1), ct);
                if (dur > 0 && lastStart > dur + 1) fits = false;
            }
            if (!fits)
            {
                var allAudio = audio.Count;
                if (allAudio >= cueTrackCount)
                {
                    AddCleanup(result, rel.Id, grp.Select(g => g.Cue.SourcePath!).Where(p => p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)));
                    rel.SplitNotes.Add($"{disc.OriginalName}: already split ({allAudio} tracks); the cue sheet is a leftover and will be cleaned up.");
                }
                else rel.AddFlag(Severity.Warning, Stages.Scan, $"{disc.OriginalName}: the cue sheet doesn't match the audio files here ({allAudio} files, cue lists {cueTrackCount} tracks). Tracks kept as they are.");
                continue;
            }

            var job = new SplitJob { Cue = best.Cue, Folder = dir, OutputFolder = dir, DiscNumber = disc.Number, ReleaseId = rel.Id };
            foreach (var (cf, candidates) in best.Map)
            {
                var pick = candidates.OrderBy(Formats.ImagePreference).First();
                job.Images[cf] = pick;
                usedImages.Add(pick);
                foreach (var other in candidates.Where(c => c != pick)) { job.Redundant.Add(other); usedImages.Add(other); }
            }
            foreach (var other in grp.Where(g => g.Cue != best.Cue).Select(g => g.Cue.SourcePath!)) job.Redundant.Add(other);
            if (best.Cue.SourcePath != null && !best.Cue.Embedded) job.Redundant.Add(best.Cue.SourcePath);

            var missing = best.Cue.Files.Where(f => f.Tracks.Any(t => t.IsAudio) && !best.Map.ContainsKey(f)).Select(f => f.Name).ToList();
            if (missing.Count > 0)
            {
                rel.AddFlag(Severity.Error, Stages.Scan, $"{disc.OriginalName}: cue references missing audio: {string.Join(", ", missing)}");
                continue;
            }
            jobs.Add(job);
        }

        foreach (var r in resolved.Where(r => r.Map.Count == 0))
        {
            // Image cue whose audio isn't here. If the folder already holds the tracks, the cue is junk.
            int need = r.Cue.AudioTracks.Count();
            var loose = audio.Except(usedImages, StringComparer.OrdinalIgnoreCase).ToList();
            if (loose.Count >= need && need > 0)
            {
                if (r.Cue.SourcePath != null && !r.Cue.Embedded) AddCleanup(result, rel.Id, new[] { r.Cue.SourcePath });
            }
            else rel.AddFlag(Severity.Error, Stages.Scan, $"{disc.OriginalName}: {Path.GetFileName(r.Cue.SourcePath)} points to audio that isn't in the folder ({string.Join(", ", r.Cue.Files.Select(f => f.Name))}).");
        }

        // Several different images (each with own cue) in one folder → one disc subfolder each.
        if (jobs.Count > 1)
        {
            int i = 0;
            foreach (var j in jobs.OrderBy(j => j.Cue.DiscNumber ?? int.MaxValue).ThenBy(j => j.Images.Values.First(), StringComparer.OrdinalIgnoreCase))
            {
                i++;
                j.DiscNumber = j.Cue.DiscNumber ?? i;
                j.OutputFolder = Path.Combine(dir, $"CD{j.DiscNumber}");
            }
            rel.SplitNotes.Add($"{disc.OriginalName}: {jobs.Count} disc images in one folder; each will be split into its own CD folder.");
        }

        // A clean copy split from: the ORIGINAL file is what goes once you confirm the split.
        foreach (var job in jobs)
            foreach (var img in job.Images.Values.Where(cleanToOriginal.ContainsKey).ToList())
                job.Redundant.Add(cleanToOriginal[img]);
        // A clean copy that isn't used as an image (no cue anywhere) is dropped again; the original stays as it was.
        foreach (var (clean, original) in cleanToOriginal.Where(kv => !usedImages.Contains(kv.Key)))
        {
            audio.Remove(clean);
            usedImages.Add(original);
            WavPackFix.DeleteCleanCopy(clean);
            result.CleanCopies.Remove(clean);
            rel.AddFlag(Severity.Error, Stages.Scan, $"{Path.GetFileName(original)}: WavPack audio with an archive glued to the front, but no cue sheet to split it with. Left alone.");
        }

        result.SplitJobs.AddRange(jobs);
        if (junkCues.Count > 0) AddCleanup(result, rel.Id, junkCues);

        // Remaining audio files become tracks of this disc (split output is added later by the splitter).
        foreach (var a in audio.Except(usedImages, StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (result.CleanupOnly.TryGetValue(rel.Id, out var cl) && cl.Contains(a, StringComparer.OrdinalIgnoreCase)) continue;
            disc.Tracks.Add(new TrackFile { Path = a, OriginalFileName = Path.GetFileName(a), Disc = disc.Number });
        }

        if (disc.Tracks.Count == 0 && jobs.Count == 0 && audio.Count == 0 && cues.Count == 0)
            rel.AddFlag(Severity.Warning, Stages.Scan, $"{disc.OriginalName}: has a cue but no audio.");
    }

    private static void AddCleanup(ScanResult r, string releaseId, IEnumerable<string> files)
    {
        if (!r.CleanupOnly.TryGetValue(releaseId, out var l)) r.CleanupOnly[releaseId] = l = new();
        foreach (var f in files) if (!l.Contains(f, StringComparer.OrdinalIgnoreCase)) l.Add(f);
    }

    /// <summary>Finds the audio file(s) a cue FILE line refers to, tolerating wrong extensions and renamed files.</summary>
    private static List<string> ResolveImage(string cueName, List<string> audio, string? cuePath, bool onlyCue)
    {
        var name = Path.GetFileName(cueName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        var exact = audio.Where(a => string.Equals(Path.GetFileName(a), name, StringComparison.OrdinalIgnoreCase)).ToList();
        var stem = Path.GetFileNameWithoutExtension(name);
        var sameStem = audio.Where(a => string.Equals(Path.GetFileNameWithoutExtension(a), stem, StringComparison.OrdinalIgnoreCase)).ToList();
        var all = exact.Union(sameStem, StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count > 0) return all;

        // The cue's own name often matches the image ("Album.cue" + "Album.flac" while FILE says "CDImage.wav").
        if (cuePath != null)
        {
            var cueStem = Path.GetFileNameWithoutExtension(cuePath);
            var byCue = audio.Where(a => string.Equals(Path.GetFileNameWithoutExtension(a), cueStem, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCue.Count > 0) return byCue;
        }

        // A single cue and a single (or one-per-format) big audio file: that's the image.
        if (onlyCue)
        {
            var stems = audio.GroupBy(a => Path.GetFileNameWithoutExtension(a), StringComparer.OrdinalIgnoreCase).ToList();
            if (stems.Count == 1) return stems[0].ToList();
        }
        return new();
    }

    private async Task<double> TotalDurationAsync(IEnumerable<string> files, CancellationToken ct)
    {
        if (_tools.Ffprobe == null) return 0;
        double total = 0;
        foreach (var f in files)
        {
            var i = await AudioProbe.ProbeAsync(_tools.Ffprobe, f, ct);
            total += i?.Duration ?? 0;
        }
        return total;
    }
}
