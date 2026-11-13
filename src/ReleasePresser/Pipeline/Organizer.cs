using Pressing.Core.Audio;
using Pressing.Core.Models;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

/// <summary>Renames files and folders, cleans out junk, and moves finished releases into place.</summary>
public sealed class Organizer
{
    private readonly PressingSettings _s;
    private readonly FileOps? _ops;

    public Organizer(PressingSettings s, FileOps? ops)
    {
        _s = s; _ops = ops;
    }

    private string Safe(string name) => TextUtil.SanitizeName(name, _s.ColonReplacement, _s.IllegalCharReplacement);

    // ------------------------------------------------------------------ names (shared by plan + apply)

    public string DiscFolderName(DiscFolder disc)
    {
        int n = disc.Tracks.Select(t => t.After.Disc).FirstOrDefault(x => x.HasValue) ?? disc.Number;
        return $"{_s.DiscFolderPrefix}{n}";
    }

    public string FileName(TrackFile t)
    {
        var a = t.After;
        var track = (a.Track ?? t.Number).ToString().PadLeft(Math.Max(1, _s.TrackNumberPadding), '0');
        var values = new Dictionary<string, string?>
        {
            ["track"] = track,
            ["title"] = a.Title ?? Path.GetFileNameWithoutExtension(t.Path),
            ["artist"] = a.Artist,
            ["albumartist"] = a.AlbumArtist,
            ["album"] = a.Album,
            ["year"] = a.Year?.ToString(),
            ["disc"] = (a.Disc ?? t.Disc).ToString(),
        };
        return Safe(TextUtil.FillTemplate(_s.FileNameTemplate, values)) + Path.GetExtension(t.Path).ToLowerInvariant();
    }

    /// <summary>Artist or label folder: decides the folder-name template and whether collection sub-folders are kept.</summary>
    public LibraryMode Mode { get; set; } = LibraryMode.Artist;

    public static string TemplateFor(PressingSettings s, LibraryMode mode) =>
        mode == LibraryMode.Collection && !string.IsNullOrWhiteSpace(s.CollectionFolderNameTemplate) ? s.CollectionFolderNameTemplate : s.FolderNameTemplate;

    /// <summary>Label folders keep releases inside their collection sub-folder.</summary>
    public bool KeepsGroups => Mode == LibraryMode.Collection && _s.KeepCollectionFolders;

    public string? FolderName(Release rel)
    {
        if (string.IsNullOrWhiteSpace(rel.Album)) return null;
        bool label = Mode == LibraryMode.Collection;
        var values = new Dictionary<string, string?>
        {
            ["album"] = rel.Album,
            ["year"] = rel.Year?.ToString(),
            ["albumartist"] = label && (rel.IsCompilation || string.Equals(rel.AlbumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase)) ? "VA" : rel.AlbumArtist,
            ["label"] = rel.Label,
            // Your own catalog numbers ("[HIPPIE DANCE 01]") win over the database's spelling.
            ["catalog"] = rel.FolderCatalog ?? rel.CatalogNumber,
            ["quality"] = Quality(rel),
            ["format"] = rel.AllTracks.Select(t => t.Codec.ToUpperInvariant()).FirstOrDefault(),
        };
        return Safe(TextUtil.FillTemplate(TemplateFor(_s, Mode), values));
    }

    /// <summary>"16B-44.1kHz" for lossless, "MP3 320" for lossy — matches the style of your existing folders.</summary>
    public static string Quality(Release rel)
    {
        if (!rel.AllTracks.Any()) return "";
        if (rel.IsLossless && rel.MaxBitDepth > 0)
            return $"{rel.MaxBitDepth}B-{(rel.MaxSampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}kHz";
        var codec = rel.AllTracks.First().Codec.ToUpperInvariant();
        var br = rel.AllTracks.Select(t => t.BitrateKbps).DefaultIfEmpty(0).Max();
        return br > 0 ? $"{codec} {br}" : codec;
    }

    /// <summary>Works out every rename and removal without touching the disk.</summary>
    /// <param name="otherReleaseDirs">Folders of other releases; any nested inside this one are never cleaned as junk.</param>
    public void Plan(Release rel, IEnumerable<string> extraJunk, IEnumerable<string>? otherReleaseDirs = null)
    {
        rel.PlannedRenames.Clear();
        foreach (var disc in rel.Discs.Where(d => d.IsSubfolder))
        {
            var to = DiscFolderName(disc);
            if (!string.Equals(Path.GetFileName(disc.Path), to, StringComparison.Ordinal))
                rel.PlannedRenames.Add(new PlannedRename("disc folder", Path.GetFileName(disc.Path), to));
        }
        foreach (var t in rel.AllTracks)
        {
            var to = FileName(t);
            if (!string.Equals(Path.GetFileName(t.Path), to, StringComparison.Ordinal))
                rel.PlannedRenames.Add(new PlannedRename("file", Path.GetFileName(t.Path), to));
        }
        rel.PlannedFolderName = FolderName(rel) ?? Path.GetFileName(rel.Path);

        var trackPaths = new HashSet<string>(rel.AllTracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var nested = (otherReleaseDirs ?? Enumerable.Empty<string>())
            .Where(d => !FileOps.PathsEqual(d, rel.Path) && d.StartsWith(rel.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        rel.PlannedJunk = extraJunk.Where(File.Exists)
            .Concat(new KeepPolicy(_s).Extras(rel.Path, nested).Where(f => !trackPaths.Contains(f)))
            .Except(rel.PendingImageRemovals, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Two releases must never end up with the same folder name (e.g. two different "Behind the Sun (2020)" editions).
    /// Runs after every release is planned: a release already using the name keeps it, the others get a suffix that
    /// says what's different about them (edition, catalog number, quality), and a note on their card.
    /// </summary>
    public void ResolveNameClashes(List<Release> releases, string root, bool replan = false)
    {
        foreach (var r in releases.Where(r => r.PlannedFolderName != null))
        {
            if (replan && r.BaseFolderName != null && r.PlannedFolderName != r.BaseFolderName)
            {
                r.PlannedFolderName = r.BaseFolderName;
                r.Flags.RemoveAll(f => f.Stage == Stages.Rename && f.Message.Contains("isn't the same release"));
            }
            r.BaseFolderName ??= r.PlannedFolderName;
        }
        var plannable = releases.Where(r => !r.Untouched && !r.Flags.Any(f => f.Severity == Severity.Error) && r.PlannedFolderName != null)
            .Where(r => !replan || (r.Include && !r.AlreadyPressed)).ToList(); // at apply time, unticked releases keep their current name
        string Key(Release r, string name) => (r.MoveToArtist ?? "").ToLowerInvariant() + "|" + (KeepsGroups ? r.Group.ToLowerInvariant() : "") + "|" + name.Trim().ToLowerInvariant();

        // Names that are taken no matter what: releases that won't be renamed, and non-release folders in the root.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in releases.Except(plannable)) taken.Add(Key(r, Path.GetFileName(r.Path)));
        var releaseDirs = new HashSet<string>(releases.Select(r => Path.GetFullPath(r.Path).TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root).Where(d => !releaseDirs.Contains(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))))
                if (!releases.Any(r => r.Path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    taken.Add(Key(new Release(), Path.GetFileName(d)));
        }
        catch { }

        foreach (var group in plannable.GroupBy(r => Key(r, r.PlannedFolderName!)).ToList())
        {
            var list = group.ToList();
            bool blocked = taken.Contains(group.Key);
            if (list.Count == 1 && !blocked) { taken.Add(group.Key); continue; }

            // Who keeps the plain name: the folder that already has it, else the best-matched one.
            var winner = blocked ? null :
                list.FirstOrDefault(r => string.Equals(Path.GetFileName(r.Path), r.PlannedFolderName, StringComparison.OrdinalIgnoreCase))
                ?? list.OrderByDescending(r => r.Match?.Confidence ?? 0).ThenByDescending(r => r.AllTracks.Count()).First();
            if (winner != null) taken.Add(group.Key);
            var baseName = list[0].PlannedFolderName!;
            foreach (var r in list.Where(r => r != winner))
            {
                var suffixes = new[]
                {
                    r.Match?.Edition, r.CatalogNumber, Quality(r), $"{r.AllTracks.Count()} tracks",
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
                string? pick = null;
                foreach (var sfx in suffixes)
                {
                    var candidate = Safe($"{baseName} [{sfx}]");
                    if (!taken.Contains(Key(r, candidate))) { pick = candidate; break; }
                }
                for (int i = 2; pick == null; i++)
                {
                    var candidate = Safe($"{baseName} ({i})");
                    if (!taken.Contains(Key(r, candidate))) pick = candidate;
                }
                taken.Add(Key(r, pick));
                var other = winner != null && !string.Equals(winner.OriginalName, baseName, StringComparison.OrdinalIgnoreCase)
                    ? $"\"{winner.OriginalName}\" (also \"{baseName}\")" : $"\"{baseName}\"";
                r.AddFlag(Severity.Warning, Stages.Rename, $"Would have the same name as {other}, but it isn't the same release — this one becomes \"{pick}\".");
                r.PlannedFolderName = pick;
            }
        }
    }

    /// <summary>True when the plan for this release changes nothing at all.</summary>
    public static bool NothingToDo(Release rel) =>
        rel.TagChangeCount == 0 && rel.PlannedRenames.Count == 0 && rel.PlannedJunk.Count == 0 && rel.PendingImageRemovals.Count == 0
        && !rel.WasSplit && rel.MoveToArtist == null
        && string.Equals(rel.PlannedFolderName, Path.GetFileName(rel.Path), StringComparison.Ordinal);

    // ------------------------------------------------------------------ rename

    public void Rename(Release rel)
    {
        // 1. Disc subfolders → CD1, CD2… (never the album name).
        foreach (var disc in rel.Discs.Where(d => d.IsSubfolder))
        {
            int n = disc.Tracks.Select(t => t.After.Disc).FirstOrDefault(x => x.HasValue) ?? disc.Number;
            disc.Number = n;
            var target = Path.Combine(Path.GetDirectoryName(disc.Path)!, DiscFolderName(disc));
            if (FileOps.PathsEqual(target, disc.Path)) continue;
            var moved = _ops!.MoveDirectory(disc.Path, target);
            Rebase(rel, disc.Path, moved, onlyDisc: disc);
        }

        // 2. Files from the template.
        foreach (var t in rel.AllTracks)
        {
            var target = Path.Combine(Path.GetDirectoryName(t.Path)!, FileName(t));
            if (!FileOps.PathsEqual(target, t.Path)) t.Path = _ops!.RenameFile(t.Path, target);
        }

        // 3. Release folder: the name shown on the review screen (already made unique there).
        if ((rel.PlannedFolderName ?? FolderName(rel)) is { } name)
        {
            var target = Path.Combine(Path.GetDirectoryName(rel.Path)!, name);
            // Another version already took this name: tell them apart by edition rather than "(2)".
            if (!FileOps.PathsEqual(target, rel.Path) && Directory.Exists(target) && !string.IsNullOrWhiteSpace(rel.Match?.Edition))
                target = Path.Combine(Path.GetDirectoryName(rel.Path)!, Safe($"{name} [{rel.Match!.Edition}]"));
            if (!FileOps.PathsEqual(target, rel.Path))
            {
                var moved = _ops!.MoveDirectory(rel.Path, target);
                if (!FileOps.PathsEqual(moved, target))
                    rel.AddFlag(Severity.Info, Stages.Rename, $"\"{name}\" already existed; used \"{Path.GetFileName(moved)}\".");
                Rebase(rel, rel.Path, moved);
            }
        }
    }

    // ------------------------------------------------------------------ cleanup

    public void Cleanup(Release rel, IEnumerable<string> extraJunk)
    {
        foreach (var f in extraJunk)
        {
            try { if (File.Exists(f)) _ops!.Remove(f); }
            catch (Exception e) { rel.AddFlag(Severity.Info, Stages.Cleanup, $"Couldn't remove {Path.GetFileName(f)}: {e.Message}"); }
        }
        // Only what the plan listed (and you saw) is removed. Audio that wasn't explicitly planned is never touched.
        if (!Directory.Exists(rel.Path)) return;
        foreach (var d in Directory.GetDirectories(rel.Path, "_pressing_tmp*", SearchOption.AllDirectories))
            try { Directory.Delete(d, true); } catch { }
        _ops!.RemoveEmptyDirectories(rel.Path);
    }

    // ------------------------------------------------------------------ place

    /// <summary>Flattens the release under the artist/label folder, or moves it to the ready folder.</summary>
    public void Place(Release rel, string root, bool keepInPlace = false)
    {
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        var mode = keepInPlace ? OutputLocation.InPlace : _s.Output;
        if (mode == OutputLocation.ReadyFolder && string.IsNullOrWhiteSpace(_s.ReadyFolder)) mode = OutputLocation.PressedSubfolder;
        var pressed = PressedRoot(_s, root);
        var parent = mode switch
        {
            OutputLocation.ReadyFolder => Path.Combine(_s.ReadyFolder, Safe(rootName)),
            OutputLocation.PressedSubfolder => pressed,
            _ => root,
        };
        if (!string.IsNullOrWhiteSpace(rel.MoveToArtist))
        {
            var artistDir = Safe(rel.MoveToArtist);
            parent = mode switch
            {
                OutputLocation.ReadyFolder => Path.Combine(_s.ReadyFolder, artistDir),
                OutputLocation.PressedSubfolder => Path.Combine(pressed, "_Other artists", artistDir),
                _ => Path.Combine(Path.GetDirectoryName(root)!, artistDir),
            };
        }
        // Label folders: "[--] Pressed\13 Hippie Dance\…", not one flat list.
        if (KeepsGroups && string.IsNullOrWhiteSpace(rel.MoveToArtist) && rel.Group.Length > 0)
            parent = Path.Combine(parent, rel.Group);
        if (FileOps.PathsEqual(rel.Path, root))
        {
            rel.AddFlag(Severity.Warning, Stages.Place, "Release is the artist folder itself; not moved.");
            rel.FinalPath = rel.Path;
            return;
        }
        var target = Path.Combine(parent, Path.GetFileName(rel.Path));
        if (!FileOps.PathsEqual(target, rel.Path))
        {
            var moved = _ops!.MoveDirectory(rel.Path, target);
            Rebase(rel, rel.Path, moved);
        }
        rel.FinalPath = rel.Path;
    }

    // ------------------------------------------------------------------ helpers

    public static string PressedRoot(PressingSettings s, string root) =>
        Path.Combine(root, string.IsNullOrWhiteSpace(s.PressedFolderName) ? PressingSettings.DefaultPressedFolderName : s.PressedFolderName.Trim());

    /// <summary>The output folder (current or old name): never scanned as releases, never shown in the Before picture.</summary>
    public static bool IsPressedFolder(PressingSettings s, string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        return string.Equals(name, Path.GetFileName(PressedRoot(s, "x")), StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, PressingSettings.LegacyPressedFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Label folders: once every release of a collection has moved into "[--] Pressed\&lt;collection&gt;", the collection's
    /// own pictures (logos) follow it and the empty folder goes. Folders that still hold any music are left alone.
    /// </summary>
    public void FollowCollections(IEnumerable<string> groups, string root)
    {
        var pressed = PressedRoot(_s, root);
        foreach (var g in groups.Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var src = Path.Combine(root, g);
            if (!Directory.Exists(src) || FileOps.PathsEqual(src, Path.Combine(pressed, g))) continue;
            var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            if (files.Any(Audio.Formats.IsAudio) || !files.All(f => KeepPolicy.IsJpg(f) || KeepPolicy.IsPng(f))) continue;
            foreach (var f in files)
            {
                var dest = Path.Combine(pressed, g, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                _ops!.RenameFile(f, dest);
            }
            _ops!.RemoveEmptyDirectories(src);
            try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); } catch { }
        }
    }

    /// <summary>
    /// An old "Pressed" output folder next to the releases becomes "[--] Pressed" (journaled, undoable),
    /// so finished albums from earlier runs and new ones end up together.
    /// </summary>
    public void MigrateLegacyPressedFolder(string root)
    {
        var legacy = Path.Combine(root, PressingSettings.LegacyPressedFolderName);
        var current = PressedRoot(_s, root);
        if (FileOps.PathsEqual(legacy, current) || !Directory.Exists(legacy)) return;
        if (!Directory.Exists(current)) { _ops!.MoveDirectory(legacy, current); return; }
        foreach (var d in Directory.GetDirectories(legacy))
            _ops!.MoveDirectory(d, Path.Combine(current, Path.GetFileName(d)));
        _ops!.RemoveEmptyDirectories(legacy);
        try { if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy); } catch { }
    }

    public static void Rebase(Release rel, string oldPrefix, string newPrefix, DiscFolder? onlyDisc = null)
    {
        string Fix(string p) =>
            p.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) && (p.Length == oldPrefix.Length || p[oldPrefix.Length] == Path.DirectorySeparatorChar)
                ? newPrefix + p[oldPrefix.Length..]
                : p;
        if (onlyDisc == null) rel.Path = Fix(rel.Path);
        foreach (var d in rel.Discs.Where(d => onlyDisc == null || d == onlyDisc))
        {
            d.Path = Fix(d.Path);
            foreach (var t in d.Tracks) t.Path = Fix(t.Path);
        }
    }

    public static bool IsKeptExtension(PressingSettings s, string path) =>
        s.KeepExtensions.Any(e => path.EndsWith(e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase)) || Formats.IsAudio(path);
}
