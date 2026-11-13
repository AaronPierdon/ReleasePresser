using System.Text.Json;
using System.Text.Json.Serialization;
using Pressing.Core.Audio;
using Pressing.Core.Metadata;
using Pressing.Core.Models;
using Pressing.Core.Tags;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

public sealed class RunOptions
{
    public string Root { get; set; } = "";
    public LibraryMode Mode { get; set; } = LibraryMode.Auto;
    /// <summary>Tidy an already-sorted folder in place: ignore the ready-for-transfer folder.</summary>
    public bool KeepInPlace { get; set; }
    /// <summary>Skip online lookups for releases whose tags are already complete (default for tidy-in-place).</summary>
    public bool TrustCompleteTags { get; set; }
    /// <summary>
    /// Look folders that an earlier run already pressed up online again. Off by default: a folder that's already in your
    /// naming format with complete tags keeps its tags (only small fixes), which makes repeat runs fast and stable.
    /// </summary>
    public bool RecheckPressed { get; set; }
    /// <summary>Folders you right-clicked → Skip on the Before page. They aren't scanned or touched at all.</summary>
    public List<string> SkipFolders { get; set; } = new();
}

/// <summary>
/// Runs the pipeline for one artist or label folder in two steps:
/// <see cref="AnalyzeAsync"/> works everything out without changing your library (it may split cue images,
/// but keeps the originals), then — after you review — <see cref="ApplyAsync"/> does exactly what you approved.
/// <see cref="DiscardAsync"/> throws a plan away and deletes anything the analysis created.
/// </summary>
public sealed class PipelineRunner
{
    private readonly PressingSettings _s;

    public event Action<string>? Log;
    public event Action<string>? StageStarted;
    public event Action<Release>? ReleaseChanged;
    public event Action<RunReport>? ReportChanged;

    public PipelineRunner(PressingSettings settings) => _s = settings.Clone();

    public static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private void Say(string m) => RunLog.Write("run", m);
    private RunLog.Timer? _stageTimer;

    /// <summary>Starts the detailed log for this run: shown live (Log event) and written to runs\&lt;id&gt;-log.txt.</summary>
    private void BeginLog(RunReport report, string what)
    {
        RunLog.Begin(Path.Combine(PressingSettings.DataDirectory, "runs", $"{report.RunId}-log.txt"), line => Log?.Invoke(line));
        RunLog.Write("run", $"════ {what}: {report.Root} ════");
        RunLog.Write("run", $"Pressing {typeof(PipelineRunner).Assembly.GetName().Version} · {Environment.MachineName} · {Environment.ProcessorCount} CPUs · {DateTime.Now:yyyy-MM-dd}");
        RunLog.Write("run", $"Settings: MusicBrainz={_s.UseMusicBrainz} Discogs={_s.UseDiscogs && !string.IsNullOrWhiteSpace(_s.DiscogsToken)} " +
                            $"AcoustID={_s.UseAcoustId && !string.IsNullOrWhiteSpace(_s.AcoustIdKey)} AI={_s.UseAi && !string.IsNullOrWhiteSpace(_s.GeminiApiKey)} ({_s.GeminiModel}) " +
                            $"albums at once={Math.Clamp(_s.ParallelAlbums, 1, 8)} accept≥{_s.AcceptConfidence} reject<{_s.MinimumConfidence} output={_s.Output} remove={_s.RemoveMode} dupes={_s.DupeCullingEnabled} onlyIdentical={_s.AutoRemoveOnlyIdentical}");
    }

    private int Workers => Math.Clamp(_s.ParallelAlbums, 1, 8);

    /// <summary>Runs <paramref name="body"/> for up to <see cref="Workers"/> albums at the same time.</summary>
    private async Task ForEachAlbumAsync<T>(IEnumerable<T> items, Func<T, Task> body, CancellationToken ct)
    {
        var list = items.ToList();
        if (Workers == 1 || list.Count < 2)
        {
            foreach (var x in list) { ct.ThrowIfCancellationRequested(); await body(x); }
            return;
        }
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct },
            async (x, _) => await body(x));
    }

    private void EndLog()
    {
        _stageTimer?.Dispose();
        _stageTimer = null;
        RunLog.WriteRaw(RunLog.Summary());
        RunLog.Write("run", "Full log saved to " + RunLog.FilePath);
        RunLog.End();
    }

    private FileOps Ops(RunReport report) =>
        new(_s, report.JournalPath ?? Path.Combine(PressingSettings.DataDirectory, "runs", $"{report.RunId}-journal.jsonl"),
            Path.Combine(Path.GetDirectoryName(report.Root) ?? report.Root, "_pressing_trash"));

    /// <summary>Analyze and then apply everything recommended (command line --apply).</summary>
    public async Task<RunReport> RunAsync(RunOptions opt, CancellationToken ct)
    {
        var report = await AnalyzeAsync(opt, ct);
        if (report.Cancelled || report.Releases.Count == 0) return report;
        return await ApplyAsync(report, ct);
    }

    // ═══════════════════════════════════════════════════════════════ analyze (no changes to your library)

    public async Task<RunReport> AnalyzeAsync(RunOptions opt, CancellationToken ct)
    {
        var root = Path.GetFullPath(opt.Root).TrimEnd(Path.DirectorySeparatorChar);
        var rootName = Path.GetFileName(root);
        var report = new RunReport { Root = root, Mode = opt.Mode, FormatVersion = 2, Phase = "planned", KeepInPlace = opt.KeepInPlace };
        Directory.CreateDirectory(Path.Combine(PressingSettings.DataDirectory, "runs"));
        using (var t = RunLog.Time("snapshot", "look at the folder (Before picture)"))
        {
            report.Before = Snapshotter.Take(root, _s, ct);
            t.Result = $"{report.Before.Folders.Count} folders, {report.Before.AudioFiles} audio files, {report.Before.ExtraFiles} extra files";
        }
        report.JournalPath = Path.Combine(PressingSettings.DataDirectory, "runs", $"{report.RunId}-journal.jsonl");
        BeginLog(report, "ANALYZE");
        var ops = Ops(report);

        var tools = Toolset.Resolve(_s);
        foreach (var m in tools.Missing()) { report.GlobalNotes.Add("Missing tool: " + m); Say("⚠ missing " + m); }
        if (tools.Ffmpeg == null || tools.Ffprobe == null)
        {
            report.GlobalNotes.Add("ffmpeg and ffprobe are required. Use Settings → Tools → Install missing tools.");
            return Finish(report, "analysis");
        }

        try
        {
            Stage(Stages.Unblock);
            if (_s.UnblockFiles)
            {
                var (u, total) = await Task.Run(() => Unblocker.UnblockTree(root, ct), ct);
                Say($"Unblocked {u} of {total} files.");
            }

            Stage(Stages.Scan);
            var scanner = new Scanner(tools, Say) { ExcludeDir = Organizer.PressedRoot(_s, root) };
            scanner.ExcludeDirs.Add(Path.Combine(root, PressingSettings.LegacyPressedFolderName));
            scanner.ExcludeDirs.AddRange(opt.SkipFolders);
            report.Skipped = opt.SkipFolders.ToList();
            foreach (var f in opt.SkipFolders) Say($"Skipping (your choice): {Path.GetFileName(f)}");
            ScanResult scan;
            using (var t = RunLog.Time("scan", "find releases and read cue sheets"))
            {
                scan = await scanner.ScanAsync(root, ct);
                t.Result = $"{scan.Releases.Count} releases, {scan.SplitJobs.Count} images to split";
            }
            foreach (var r in scan.Releases)
                RunLog.Write("scan", $"{r.OriginalName}: {r.Discs.Count} disc folder(s), {r.AllTracks.Count()} tracks" +
                                     (r.SplitNotes.Count > 0 ? " · " + string.Join(" · ", r.SplitNotes) : "") +
                                     string.Concat(r.Flags.Select(f => $" · {f.Severity}: {f.Message}")));
            report.Releases = scan.Releases;
            Say($"Found {scan.Releases.Count} releases, {scan.SplitJobs.Count} cue images to split.");
            if (scan.LooseAudio.Count > 0)
            {
                var note = $"{scan.LooseAudio.Count} audio file(s) sit loose in the folder itself and were left alone — put each in its own release folder to have it pressed: " +
                           string.Join(", ", scan.LooseAudio.Take(5).Select(Path.GetFileName)) + (scan.LooseAudio.Count > 5 ? " …" : "");
                report.GlobalNotes.Add(note);
                Say(note);
            }
            Changed(report);
            await ForEachAlbumAsync(report.Releases, async rel =>
            {
                using var t = RunLog.Time("probe", $"read audio info + tags: {rel.OriginalName} ({rel.AllTracks.Count()} files)", logStart: false);
                foreach (var tr in rel.AllTracks)
                {
                    await ProbeTrackAsync(tools, tr, ct);
                    // Audio ffprobe can't read is never tagged, renamed or moved.
                    if (tr.Codec.Length == 0 && tr.Duration <= 0)
                        rel.AddFlag(Severity.Error, Stages.Scan, $"Can't read {Path.GetFileName(tr.Path)} (corrupt or unsupported format) — release left alone.");
                }
                t.Result = rel.FormatSummary;
            }, ct);

            // Splits are verified and the originals are KEPT until you confirm.
            Stage(Stages.Split);
            var splitter = new Splitter(tools, _s, ops, Say) { DeferRemoval = true };
            // Different albums split side by side; images of the same album one after another.
            await ForEachAlbumAsync(scan.SplitJobs.GroupBy(j => j.ReleaseId).ToList(), async jobs =>
            {
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                var rel = report.Releases.First(r => r.Id == job.ReleaseId);
                Touch(rel, Stages.Split, ReleaseState.Running, 0);
                Say($"Splitting {rel.OriginalName} / {Path.GetFileName(job.Images.Values.First())}");
                var progress = new Progress<double>(p => Touch(rel, Stages.Split, ReleaseState.Running, p));
                SplitOutcome res;
                using (var t = RunLog.Time("split", $"split {rel.OriginalName} / {Path.GetFileName(job.Images.Values.First())}"))
                {
                    res = await splitter.SplitAsync(job, rel, progress, ct);
                    t.Result = res.Success ? res.Summary : "FAILED";
                }
                if (res.Success)
                {
                    rel.WasSplit = true;
                    rel.SplitNotes.Add(res.Summary);
                    rel.MusicBrainzDiscId ??= res.DiscId;
                    rel.CdToc ??= res.Toc;
                    rel.SplitOutputs.AddRange(res.Tracks.Select(t => t.Path));
                    rel.PendingImageRemovals.AddRange(res.ToRemove);
                    AttachSplitTracks(rel, job, res);
                    Say("  " + res.Summary);
                }
                Touch(rel, Stages.Split, ReleaseState.Running, 1);
            }
            }, ct);

            foreach (var c in scan.CleanCopies) WavPackFix.DeleteCleanCopy(c);

            foreach (var r in report.Releases.Where(r => r.Flags.Any(f => f.Severity == Severity.Error) || !r.AllTracks.Any()))
            {
                r.State = ReleaseState.Flagged;
                r.Include = false;
            }
            var active = report.Releases.Where(r => r.State != ReleaseState.Flagged).ToList();

            if (opt.Mode == LibraryMode.Auto)
            {
                var artists = active.Select(r => r.AllTracks.Select(t => t.Before.AlbumArtist ?? TextUtil.SplitArtists(t.Before.Artist).FirstOrDefault())
                        .Where(a => !string.IsNullOrWhiteSpace(a)).GroupBy(a => TextUtil.Normalize(a)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault())
                    .Where(a => a != null).Distinct().Count();
                int byRootArtist = active.Count(r => r.AllTracks.Any(t =>
                    ArtistMatches(t.Before.AlbumArtist, rootName) || ArtistMatches(TextUtil.SplitArtists(t.Before.Artist).FirstOrDefault(), rootName)));
                bool rootIsArtist = active.Count > 0 && byRootArtist >= active.Count * 0.5;
                bool manyCompilations = active.Count(r => r.AllTracks.Select(t => TextUtil.Normalize(TextUtil.SplitArtists(t.Before.Artist).FirstOrDefault())).Distinct().Count() > 3) > active.Count / 2;
                // Share of releases by the most common artist: an artist folder is dominated by one name, a label isn't.
                int topArtistReleases = active.Select(r => r.AllTracks.Select(t => t.Before.AlbumArtist ?? TextUtil.SplitArtists(t.Before.Artist).FirstOrDefault())
                        .Where(a => !string.IsNullOrWhiteSpace(a)).GroupBy(a => TextUtil.Normalize(a)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault())
                    .Where(a => a != null).GroupBy(a => a).Select(g => g.Count()).DefaultIfEmpty(0).Max();
                bool noDominantArtist = active.Count >= 3 && artists >= 3 && topArtistReleases < active.Count * 0.5;
                // Releases sitting inside collection folders ("01 Kompakt\[KOMPAKT 001] …") are a label's layout.
                bool inCollections = report.Releases.Count > 0 && report.Releases.Count(r => r.Group.Length > 0) >= report.Releases.Count * 0.5;
                bool namedLabel = System.Text.RegularExpressions.Regex.IsMatch(rootName, @"\b(label|records|recordings)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                report.Mode = !rootIsArtist && (namedLabel || inCollections || noDominantArtist || manyCompilations)
                    ? LibraryMode.Collection : LibraryMode.Artist;
                Say($"Library type: {report.Mode} (auto: {artists} artists, top artist on {topArtistReleases} of {active.Count} releases, " +
                    $"{report.Releases.Count(r => r.Group.Length > 0)} in collection folders, root artist on {byRootArtist}{(namedLabel ? ", folder named as a label" : "")})");
            }

            // Metadata: worked out, NOT written.
            Stage(Stages.Metadata);
            var resolver = new MetadataResolver(_s, tools, Say);
            var nameFormat = Snapshotter.TemplateRegex(Organizer.TemplateFor(_s, report.Mode));
            int trusted = 0, pressedBefore = 0;
            await ForEachAlbumAsync(active, async rel =>
            {
                Touch(rel, Stages.Metadata, ReleaseState.Running, 0.3);
                rel.TagsComplete = MetadataResolver.HasCompleteTags(rel);
                // Pressed by an earlier run: already in your naming format with complete tags → keep its tags, no lookups.
                bool earlierPress = !opt.RecheckPressed && rel.TagsComplete && !rel.WasSplit && nameFormat.IsMatch(Path.GetFileName(rel.Path));
                if (earlierPress || (opt.TrustCompleteTags && rel.TagsComplete && !rel.WasSplit))
                {
                    resolver.KeepExisting(rel, report.Mode, rootName);
                    if (earlierPress) Interlocked.Increment(ref pressedBefore); else Interlocked.Increment(ref trusted);
                    RunLog.Write("metadata", $"{rel.OriginalName}: {(earlierPress ? "already in your format with complete tags" : "tags complete (tidy in place)")} — kept existing tags, no lookups");
                }
                else
                {
                    using var t = RunLog.Time("metadata", $"identify {rel.OriginalName}");
                    await resolver.ResolveAsync(rel, report.Mode, rootName, ct);
                    t.Result = rel.Match is { } mm ? $"{mm.Source} {mm.Confidence:0.00}" + (mm.BonusTracks > 0 ? $", {mm.BonusTracks} bonus" : "") : "no match";
                }
                // CheckArtist runs AFTER the loop, once we've inferred the folder's real main artist from all the metadata.
                // Weak matches are planned but switched off until you tick them.
                if (rel.Match is { } m && m.Source != "Existing tags" && m.Confidence < _s.AcceptConfidence) rel.Include = false;
                if (rel.Untouched) rel.Include = false;
                Touch(rel, Stages.Metadata, ReleaseState.Running, 1);
            }, ct);
            if (trusted > 0) Say($"{trusted} releases already had complete tags; kept them without online lookups.");
            if (pressedBefore > 0) Say($"{pressedBefore} releases look pressed by an earlier run; kept their tags without online lookups.");
            // --- MAW: work out the folder's real MAIN ARTIST from the metadata we just gathered, instead of trusting the
            // folder name. Everyone who is (or features) the main artist consolidates under one canonical spelling; a
            // release clearly by someone else is flagged and suggested for its own folder. Falls back to the folder name
            // when there's no clear main artist, so a normal single-artist folder behaves exactly as before. ---
            string folderArtist = rootName;
            if (report.Mode == LibraryMode.Artist)
            {
                folderArtist = InferMainArtist(active, rootName);
                if (!string.Equals(folderArtist, rootName, StringComparison.OrdinalIgnoreCase))
                    Say($"Main artist for this folder looks like \"{folderArtist}\". Consolidating everyone under it; releases clearly by someone else are flagged for their own folder.");
                foreach (var rel in active)
                {
                    CheckArtist(rel, folderArtist);
                    if (rel.Untouched) rel.Include = false;
                }
            }
            Changed(report);

            if (report.Mode == LibraryMode.Artist && _s.OneAlbumArtistPerFolder)
                using (RunLog.Time("metadata", "one album artist for the whole folder", logStart: false))
                    resolver.UnifyFolderArtist(active, folderArtist);

            var planned = active.Where(r => !r.Untouched).ToList();

            Stage(Stages.Dupes);
            if (_s.DupeCullingEnabled)
            {
                var dupes = new DupeResolver(_s, tools, null, resolver.Ai, Say);
                report.DupeGroups = dupes.FindGroups(planned);
                await ForEachAlbumAsync(report.DupeGroups, async g =>
                {
                    Say($"Duplicates: {g.Title} ({g.Versions.Count} versions: {string.Join(" | ", g.Versions.Select(v => v.Name))})");
                    using (RunLog.Time("dupes", $"compare versions of {g.Title}", logStart: false))
                        await dupes.RecommendAsync(g, report.Releases, ct);
                    Say($"  → {g.DecidedBy}: {g.Reason}");
                }, ct);
            }
            using (RunLog.Time("dupes", $"redundancy check across {planned.Count} releases", logStart: false))
                RedundancyFinder.Mark(planned);
            foreach (var r in planned.Where(r => r.Redundant)) Say($"  redundant: {r.OriginalName} — all tracks also on {string.Join(", ", r.ContainedIn.Take(3).Select(c => c.Name))}");
            int redundant = planned.Count(r => r.Redundant);
            if (redundant > 0) Say($"{redundant} releases contain only tracks you already have elsewhere (listed for you to decide).");

            Stage(Stages.Rename);
            var org = new Organizer(_s, null) { Mode = report.Mode };
            var releaseDirs = report.Releases.Select(r => r.Path).ToList();
            using (RunLog.Time("plan", $"plan names and cleanup for {planned.Count} releases", logStart: false))
            {
                foreach (var rel in planned)
                    org.Plan(rel, scan.CleanupOnly.TryGetValue(rel.Id, out var j) ? j : Enumerable.Empty<string>(), releaseDirs);
                org.ResolveNameClashes(report.Releases, root);
            }
            foreach (var rel in planned)
            {
                if (Organizer.NothingToDo(rel))
                {
                    // Already exactly right: nothing to apply, nothing to show unless you ask for it.
                    rel.AlreadyPressed = true;
                    rel.Include = false;
                    Touch(rel, "Already pressed", ReleaseState.Done, 1);
                    continue;
                }
                Touch(rel, "Planned", rel.Flags.Any(f => f.Severity == Severity.Warning) ? ReleaseState.Flagged : ReleaseState.Pending, 1);
            }
            using (RunLog.Time("safety", "nothing-lost check (preview)", logStart: false))
                report.Audit = SafetyNet.Check(report, _s).Audit;
            foreach (var rel in planned.Where(r => !r.AlreadyPressed))
                RunLog.Write("plan", $"{rel.OriginalName} → \"{rel.PlannedFolderName}\": {rel.TagChangeCount} tag changes, {rel.PlannedRenames.Count} renames, {rel.PlannedJunk.Count} extra files" +
                                     (rel.Include ? "" : " · UNTICKED") + string.Concat(rel.Flags.Where(f => f.Severity != Severity.Info).Select(f => $" · {f.Severity}: {f.Message}")));
            int already = planned.Count(r => r.AlreadyPressed);
            if (already > 0) Say($"{already} releases are already pressed — nothing to do there.");
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            Say("Cancelled. Nothing in your library was changed.");
            await DiscardAsync(report, CancellationToken.None);
        }
        catch (Exception e)
        {
            report.GlobalNotes.Add("Analysis stopped by an unexpected error: " + e);
            Say("ERROR: " + e.Message);
        }
        return Finish(report, "analysis");
    }

    // ═══════════════════════════════════════════════════════════════ apply (after you confirm)

    public async Task<RunReport> ApplyAsync(RunReport report, CancellationToken ct)
    {
        if (report.Phase != "planned") throw new InvalidOperationException($"This plan was already {report.Phase}.");
        BeginLog(report, "APPLY");
        var ops = Ops(report);
        var tools = Toolset.Resolve(_s);
        var included = report.Releases.Where(r => r.Include && !r.Untouched && r.State != ReleaseState.Culled).ToList();
        Say($"Applying to {included.Count} releases (tags={report.ApplyTags}, renames={report.ApplyRenames}, cleanup={report.ApplyCleanup}, place={report.ApplyPlace}).");

        try
        {
            // Cue images: remove for included releases; for excluded ones, take the split tracks back out.
            Stage(Stages.Split);
            foreach (var r in report.Releases.Where(r => r.WasSplit))
            {
                if (r.Include)
                    foreach (var f in r.PendingImageRemovals) Try(r, Stages.Split, () => ops.Remove(f));
                else
                    foreach (var f in r.SplitOutputs) Try(r, Stages.Split, () => { if (File.Exists(f)) File.Delete(f); });
            }

            Stage(Stages.Dupes);
            // Nothing-lost check across every removal together; anything unsafe is kept and flagged.
            var (_, preAudit) = SafetyNet.Check(report, _s);
            foreach (var b in preAudit.Blocked) Say("  🛡 " + b);
            var dupes = new DupeResolver(_s, tools, ops, null, Say);
            foreach (var g in report.DupeGroups.Where(g => !g.Resolved))
                dupes.Apply(g, report.Releases);
            foreach (var r in report.Releases.Where(r => r.RemoveAsRedundant && r.State != ReleaseState.Culled && r.Protected == null))
            {
                Try(r, Stages.Dupes, () =>
                {
                    using (RunLog.Time("remove", $"remove redundant {Path.GetFileName(r.Path)}", logStart: false))
                        ops.Remove(r.Path);
                    r.State = ReleaseState.Culled;
                    r.DupeDecision = "Removed: every track is already on " + string.Join(", ", r.ContainedIn.Take(3).Select(c => $"\"{c.Name}\""));
                    Say($"  removed redundant {Path.GetFileName(r.Path)}");
                });
            }
            included = included.Where(r => r.State != ReleaseState.Culled).ToList();
            // A name clash with a folder that just got removed is gone: give the survivor its plain name back.
            new Organizer(_s, null) { Mode = report.Mode }.ResolveNameClashes(report.Releases.Where(r => r.State != ReleaseState.Culled).ToList(), report.Root, replan: true);
            Stage(Stages.Metadata);
            if (report.ApplyTags)
            {
                var resolver = new MetadataResolver(_s, tools, Say);
                foreach (var r in included)
                {
                    ct.ThrowIfCancellationRequested();
                    var changing = r.AllTracks.Where(t => t.TagsDiffer).ToList();
                    using (RunLog.Time("tags", $"write tags: {r.OriginalName} ({changing.Count} files)", logStart: false))
                        resolver.WriteTags(r);
                    foreach (var t in changing) t.TagsWritten = true;
                    Touch(r, Stages.Metadata, ReleaseState.Running, 0.3);
                }
            }


            var org = new Organizer(_s, ops) { Mode = report.Mode };
            Stage(Stages.Cleanup);
            if (report.ApplyCleanup)
                foreach (var r in included)
                    using (RunLog.Time("cleanup", $"remove {r.PlannedJunk.Count} extra files: {r.OriginalName}", logStart: false))
                        Try(r, Stages.Cleanup, () => org.Cleanup(r, r.PlannedJunk));

            Stage(Stages.Rename);
            if (report.ApplyRenames)
                foreach (var r in included)
                {
                    ct.ThrowIfCancellationRequested();
                    using (RunLog.Time("rename", $"rename: {r.OriginalName}", logStart: false))
                        Try(r, Stages.Rename, () => org.Rename(r));
                }

            Stage(Stages.Place);
            if (report.ApplyPlace && !report.KeepInPlace && _s.Output == OutputLocation.PressedSubfolder)
                Try(new Release(), Stages.Place, () => new Organizer(_s, ops).MigrateLegacyPressedFolder(report.Root));
            foreach (var r in included)
            {
                if (report.ApplyPlace)
                    using (RunLog.Time("place", $"place: {r.OriginalName}", logStart: false))
                        Try(r, Stages.Place, () => org.Place(r, report.Root, report.KeepInPlace));
                r.FinalPath ??= r.Path;
                Touch(r, Stages.Place, Final(r), 1);
            }
            if (report.ApplyPlace && org.KeepsGroups && !report.KeepInPlace && _s.Output == OutputLocation.PressedSubfolder)
                Try(new Release(), Stages.Place, () => org.FollowCollections(included.Select(r => r.Group), report.Root));
            if (report.ApplyPlace) ops.RemoveEmptyDirectories(report.Root);
            foreach (var r in report.Releases.Where(r => !included.Contains(r)))
            {
                r.FinalPath ??= r.Path;
                Touch(r, r.Stage, r.State == ReleaseState.Culled ? ReleaseState.Culled : r.Include || r.AlreadyPressed ? Final(r) : ReleaseState.Flagged, 1);
            }
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            Say("Stopped part-way. Everything done so far is in the journal and can be undone.");
        }
        report.Phase = "applied";
        // The After picture: a fresh look at the disk, plus the final nothing-lost check.
        try
        {
            using var snapT = RunLog.Time("snapshot", "look at the folder again (After picture)", logStart: false);
            report.After = Snapshotter.TakeMany(report.Root, Snapshotter.OutputParents(report), _s);
            report.Audit = SafetyNet.Verify(report);
            Say(report.Audit.Ok
                ? $"🛡 Nothing lost: {report.Audit.InPlace} recordings in place, {report.Audit.CoveredElsewhere} in removed folders that also live in kept ones."
                : $"⚠ {report.Audit.Lost.Count} recording(s) can't be found — see the After page.");
        }
        catch (Exception e) { report.GlobalNotes.Add("Couldn't take the After snapshot: " + e.Message); }
        await Task.CompletedTask;
        return Finish(report, "apply");
    }

    /// <summary>Throws a plan away: deletes tracks the analysis split out (originals were kept). Nothing else was changed.</summary>
    public Task DiscardAsync(RunReport report, CancellationToken ct)
    {
        foreach (var r in report.Releases)
            foreach (var f in r.SplitOutputs)
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        foreach (var r in report.Releases.Where(r => r.WasSplit))
            foreach (var d in r.Discs.Where(d => d.IsSubfolder && Directory.Exists(d.Path)))
                try { if (!Directory.EnumerateFileSystemEntries(d.Path).Any()) Directory.Delete(d.Path); } catch { }
        report.Phase = "discarded";
        Save(report);
        return Task.CompletedTask;
    }

    /// <summary>Writes the original tags back to every file this run (or a run of the first version) tagged.</summary>
    public static (int Restored, List<string> Problems) RestoreTags(RunReport report, PressingSettings s)
    {
        int n = 0;
        var problems = new List<string>();
        foreach (var t in report.Releases.SelectMany(r => r.AllTracks))
        {
            bool written = t.TagsWritten || (report.FormatVersion < 2 && t.After.Title != null);
            if (!written) continue;
            if (!File.Exists(t.Path)) { problems.Add("Missing: " + t.Path); continue; }
            try { TagIO.Write(t.Path, t.Before, s); t.TagsWritten = false; n++; }
            catch (Exception e) { problems.Add($"{Path.GetFileName(t.Path)}: {e.Message}"); }
        }
        return (n, problems);
    }

    private void Try(Release r, string stage, Action a)
    {
        try { a(); }
        catch (Exception e) { r.AddFlag(Severity.Warning, stage, e.Message); Say($"  ⚠ {r.OriginalName}: {e.Message}"); }
    }

    private sealed class ArtistTally { public readonly Dictionary<string, int> Spellings = new(StringComparer.Ordinal); }

    /// <summary>
    /// MAW: infer the folder's main artist from the releases themselves, not the folder's name. It tallies every credited
    /// artist, then (using the same containment rules as CheckArtist, so "Bruce Brubaker &amp; Max Cooper" counts toward
    /// "Max Cooper") finds the one that appears across the most releases. Needs to have "seen enough" — the winner must be
    /// on at least two releases and a third of those that have an artist — otherwise it falls back to the folder name, so
    /// a plain single-artist folder behaves exactly as it always did. A correctly-named folder keeps its own spelling.
    /// </summary>
    private string InferMainArtist(List<Release> active, string rootName)
    {
        var withArtist = active.Where(r => !r.IsCompilation && !string.IsNullOrWhiteSpace(r.AlbumArtist)).ToList();
        if (withArtist.Count < 2) return rootName;

        var tally = new Dictionary<string, ArtistTally>(StringComparer.Ordinal);
        foreach (var r in withArtist)
            foreach (var tok in TextUtil.SplitArtists(r.AlbumArtist))
            {
                var key = TextUtil.Normalize(tok);
                if (key.Length < 2) continue;
                if (!tally.TryGetValue(key, out var c)) { c = new ArtistTally(); tally[key] = c; }
                var raw = tok.Trim();
                c.Spellings[raw] = c.Spellings.TryGetValue(raw, out var n) ? n + 1 : 1;
            }
        if (tally.Count == 0) return rootName;

        int bestCov = 0; string bestDisplay = null;
        foreach (var kv in tally)
        {
            var display = kv.Value.Spellings.OrderByDescending(s => s.Value).ThenByDescending(s => s.Key.Length).First().Key;
            int cov = withArtist.Count(r => ArtistMatches(r.AlbumArtist, display));
            if (cov > bestCov) { bestCov = cov; bestDisplay = display; }
        }
        if (bestDisplay == null || bestCov < 2 || bestCov < withArtist.Count / 3.0) return rootName;

        // Honor a folder that is already named after the artist: keep the folder's own spelling when it covers comparably.
        if (!string.IsNullOrWhiteSpace(rootName))
        {
            int rootCov = withArtist.Count(r => ArtistMatches(r.AlbumArtist, rootName));
            if (rootCov >= 2 && rootCov >= bestCov * 0.75) return rootName.Trim();
        }
        return bestDisplay;
    }

    public static bool ArtistMatches(string? artist, string rootName)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(rootName)) return false;
        if (TextUtil.Similarity(artist, rootName) >= 0.85) return true;
        var root = TextUtil.Normalize(rootName);
        return root.Length >= 3 && System.Text.RegularExpressions.Regex.IsMatch(TextUtil.Normalize(artist), $@"\b{System.Text.RegularExpressions.Regex.Escape(root)}\b");
    }

    /// <summary>
    /// In an artist folder, spots albums that belong to someone else. Collaborations and albums where the
    /// folder's artist is credited on most tracks are fine. Only acts when the evidence is solid
    /// (confident online match, or the files' own tags agree); otherwise it just flags.
    /// </summary>
    private void CheckArtist(Release rel, string rootName)
    {
        if (rel.IsCompilation || string.IsNullOrWhiteSpace(rel.AlbumArtist) || ArtistMatches(rel.AlbumArtist, rootName)) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(rel.AlbumArtist.Trim(), @"^(various( artists)?|va|v\.a\.)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            rel.IsCompilation = true; // "Compiled by / Mixed by <artist>" compilations belong with the artist
            return;
        }
        // "Mixed by …", "Selected by …", "Compiled by …", "(… rework)" releases that credit the folder's artist stay.
        if (System.Text.RegularExpressions.Regex.IsMatch(rel.Album ?? "", $@"\b(mixed|compiled|selected|presented|curated)\s+by\s+{System.Text.RegularExpressions.Regex.Escape(rootName)}\b|\b{System.Text.RegularExpressions.Regex.Escape(rootName)}\s+(rework|remix|mix|edit|presents)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            rel.AddFlag(Severity.Info, Stages.Metadata, $"Album artist is {rel.AlbumArtist}, but the release credits {rootName} — kept in this folder.");
            return;
        }
        var tracks = rel.AllTracks.ToList();
        int credited = tracks.Count(t => ArtistMatches(t.After.Artist, rootName));
        if (tracks.Count > 0 && credited >= tracks.Count / 2.0) return;

        int tagsSayOther = tracks.Count(t => !ArtistMatches(t.Before.AlbumArtist ?? t.Before.Artist, rootName) && !string.IsNullOrWhiteSpace(t.Before.AlbumArtist ?? t.Before.Artist));
        bool solid = (rel.Match?.Confidence ?? 0) >= _s.AcceptConfidence || tagsSayOther > tracks.Count / 2;
        if (!solid)
        {
            rel.AddFlag(Severity.Warning, Stages.Metadata, $"Might be by {rel.AlbumArtist}, not {rootName} (weak evidence) — left in this folder.");
            return;
        }
        switch (_s.WrongArtistAction)
        {
            case WrongArtistAction.MoveToArtistFolder:
                rel.MoveToArtist = rel.AlbumArtist;
                rel.AddFlag(Severity.Warning, Stages.Metadata, $"Album is by {rel.AlbumArtist}, not {rootName} — moving it to a \"{rel.AlbumArtist}\" folder next to this one.");
                break;
            case WrongArtistAction.LeaveUntouched:
                rel.Untouched = true;
                rel.Include = false;
                foreach (var t in tracks) t.After = t.Before.Clone();
                rel.AddFlag(Severity.Warning, Stages.Metadata, $"Album is by {rel.AlbumArtist}, not {rootName} — left completely untouched.");
                break;
            default:
                rel.AddFlag(Severity.Warning, Stages.Metadata, $"Album is by {rel.AlbumArtist}, not {rootName} — tags fixed, left in this folder.");
                break;
        }
    }

    private RunReport Finish(RunReport report, string what)
    {
        report.Finished = DateTime.Now;
        Save(report);
        Changed(report);
        if (what == "analysis")
            Say($"Analysis done: {report.Releases.Count} releases, {report.Releases.Count(r => r.Include)} with planned changes, {report.Releases.Count(r => r.AlreadyPressed)} already pressed, {report.DupeGroups.Count} duplicate groups, {report.Releases.Count(r => r.Redundant)} redundant. Nothing has been changed yet.");
        else
            Say($"Applied. {report.CountSplit} split, {report.CountCulled} removed, {report.CountFlagged} need a look.");
        EndLog();
        return report;
    }

    private static ReleaseState Final(Release r) =>
        r.State == ReleaseState.Culled ? ReleaseState.Culled :
        r.Flags.Any(f => f.Severity == Severity.Error) ? ReleaseState.Failed :
        r.Flags.Any(f => f.Severity == Severity.Warning) ? ReleaseState.Flagged : ReleaseState.Done;

    public static string ReportPath(RunReport r) => Path.Combine(PressingSettings.DataDirectory, "runs", $"{r.RunId}-report.json");

    public static void Save(RunReport r)
    {
        try { File.WriteAllText(ReportPath(r), JsonSerializer.Serialize(r, ReportJson)); } catch { }
    }

    public static RunReport? LoadReport(string path)
    {
        try { return JsonSerializer.Deserialize<RunReport>(File.ReadAllText(path), ReportJson); } catch { return null; }
    }

    private void Stage(string s)
    {
        _stageTimer?.Dispose();
        StageStarted?.Invoke(s);
        _stageTimer = RunLog.Time("stage", s);
    }
    private void Changed(RunReport r) => ReportChanged?.Invoke(r);

    private void Touch(Release r, string stage, ReleaseState? state, double progress)
    {
        r.Stage = stage;
        if (state.HasValue && r.State != ReleaseState.Culled) r.State = state.Value;
        r.Progress = progress;
        ReleaseChanged?.Invoke(r);
    }

    private static async Task ProbeTrackAsync(Toolset tools, TrackFile t, CancellationToken ct)
    {
        var info = await AudioProbe.ProbeAsync(tools.Ffprobe!, t.Path, ct);
        if (info != null)
        {
            t.Duration = info.Duration;
            t.Codec = info.Codec;
            t.Lossless = info.Lossless;
            t.SampleRate = info.SampleRate;
            t.BitDepth = info.BitDepth;
            t.BitrateKbps = info.BitrateKbps;
        }
        t.Before = TagIO.Read(t.Path);
        t.Number = t.Before.Track ?? TextUtil.ParseLeadingInt(Path.GetFileName(t.Path)) ?? 0;
        if (t.Before.Disc is > 0 && !t.Path.Contains("CD", StringComparison.OrdinalIgnoreCase)) { /* keep folder-derived disc */ }
    }

    private static void AttachSplitTracks(Release rel, SplitJob job, SplitOutcome res)
    {
        var disc = rel.Discs.FirstOrDefault(d => FileOps.PathsEqual(d.Path, job.OutputFolder));
        if (disc == null)
        {
            disc = new DiscFolder
            {
                Path = job.OutputFolder,
                OriginalName = Path.GetFileName(job.OutputFolder),
                IsSubfolder = true,
                Number = job.DiscNumber,
            };
            rel.Discs.Add(disc);
            rel.Discs.Sort((a, b) => a.Number.CompareTo(b.Number));
        }
        disc.Tracks.AddRange(res.Tracks);
        // A folder that held several images is now just a parent: drop it as a disc if it has no tracks.
        rel.Discs.RemoveAll(d => d.Tracks.Count == 0 && rel.Discs.Any(o => o != d && o.Tracks.Count > 0));
    }
}
