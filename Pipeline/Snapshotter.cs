using System.Text.RegularExpressions;
using Pressing.Core.Audio;
using Pressing.Core.Models;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

/// <summary>Takes a <see cref="FolderSnapshot"/> of an artist/label folder straight from the disk.</summary>
public static class Snapshotter
{
    public static FolderSnapshot Take(string root, PressingSettings s, CancellationToken ct = default)
    {
        var snap = new FolderSnapshot { Root = root, TakenAt = DateTime.Now };
        if (!Directory.Exists(root)) return snap;
        var policy = new KeepPolicy(s);
        var nameRx = TemplateRegex(s.FolderNameTemplate);
        var nameRx2 = TemplateRegex(s.CollectionFolderNameTemplate);
        var fileRx = new Regex(@"^\d{1,3}\b", RegexOptions.Compiled);

        snap.LooseFiles = SafeFiles(root, SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
            .Where(n => !n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && !n.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        AddFolders(snap, root, "", 0, s, policy, nameRx, nameRx2, fileRx, ct);
        return snap;
    }

    /// <summary>
    /// One tile per release folder. A folder that only holds other release folders (a label's collection, like
    /// "13 Hippie Dance") isn't a tile itself: its releases are shown, grouped under its name.
    /// </summary>
    private static void AddFolders(FolderSnapshot snap, string dir0, string group, int depth, PressingSettings s, KeepPolicy policy,
                                   Regex nameRx, Regex nameRx2, Regex fileRx, CancellationToken ct)
    {
        foreach (var dir in SafeDirs(dir0).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (name.StartsWith("_pressing", StringComparison.OrdinalIgnoreCase)) continue;
            if (depth == 0 && Organizer.IsPressedFolder(s, dir))
            {
                var inside = new FolderSnapshot();
                AddFolders(inside, dir, "", 1, s, policy, nameRx, nameRx2, fileRx, ct);
                snap.PressedAlready += inside.Folders.Count;
                continue;
            }
            if (depth < 3 && IsContainer(dir))
            {
                AddFolders(snap, dir, group.Length == 0 ? name : Path.Combine(group, name), depth + 1, s, policy, nameRx, nameRx2, fileRx, ct);
                continue;
            }
            var tile = new FolderTile { Name = name, Path = dir, Group = group };
            var files = SafeFiles(dir, SearchOption.AllDirectories);
            // A folder holding nothing but pictures (a collection's logo after its releases moved on) isn't a release.
            if (!files.Any(Formats.IsAudio) && files.All(f => KeepPolicy.IsJpg(f) || KeepPolicy.IsPng(f))) continue;
            foreach (var f in files)
            {
                if (Formats.IsAudio(f))
                {
                    tile.AudioFiles++;
                    try { tile.AudioBytes += new FileInfo(f).Length; } catch { }
                    var fmt = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                    tile.AudioByFormat[fmt] = tile.AudioByFormat.GetValueOrDefault(fmt) + 1;
                }
                else if (KeepPolicy.IsJpg(f)) tile.Jpgs++;
                else if (KeepPolicy.IsPng(f)) tile.Pngs++;
                else if (f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)) tile.Cues++;
            }
            tile.ExtraFiles = policy.Extras(dir).Select(f => Path.GetRelativePath(dir, f)).ToList();
            foreach (var sub in SafeDirs(dir))
            {
                var sn = Path.GetFileName(sub);
                if (KeepPolicy.IsDiscFolder(sn)) tile.DiscFolders.Add(sn); else tile.OtherFolders.Add(sn);
            }

            // Does it look like a finished release?
            var audioNames = files.Where(Formats.IsAudio).Select(Path.GetFileName).ToList();
            bool numbered = audioNames.Count > 0 && audioNames.All(n => fileRx.IsMatch(n!));
            bool singleImage = tile.AudioFiles == 1 && tile.Cues > 0;
            tile.Problem =
                tile.AudioFiles == 0 ? "no audio" :
                singleImage ? "cue image (needs splitting)" :
                tile.OtherFolders.Count > 0 ? $"extra folder{(tile.OtherFolders.Count == 1 ? "" : "s")}: {string.Join(", ", tile.OtherFolders.Take(2))}" :
                tile.ExtraFiles.Count > 0 ? $"{tile.ExtraFiles.Count} extra file{(tile.ExtraFiles.Count == 1 ? "" : "s")} ({Describe(tile.ExtraFiles)})" :
                !nameRx.IsMatch(name) && !nameRx2.IsMatch(name) ? "name isn't in your format" :
                !numbered ? "track files not numbered" : "";
            tile.LooksPressed = tile.Problem.Length == 0;
            snap.Folders.Add(tile);
        }
    }

    /// <summary>No audio of its own and no CD folders, but sub-folders that are releases: a collection, not a release.</summary>
    private static bool IsContainer(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any(Formats.IsAudio)) return false;
            var subs = Directory.EnumerateDirectories(dir).ToList();
            if (subs.Count == 0 || subs.Any(d => KeepPolicy.IsDiscFolder(Path.GetFileName(d)))) return false;
            return subs.Any(d => Directory.EnumerateFiles(d).Any(Formats.IsAudio)
                                 || Directory.EnumerateDirectories(d).Any(x => KeepPolicy.IsDiscFolder(Path.GetFileName(x)))
                                 || Directory.EnumerateDirectories(d).Any(x => Directory.EnumerateFiles(x).Any(Formats.IsAudio)));
        }
        catch { return false; }
    }

    /// <summary>
    /// After a run, releases may live in more than one place (the folder itself, a Pressed subfolder, the ready folder,
    /// another artist's folder). This looks at all of them and shows them as one picture, rooted at <paramref name="root"/>.
    /// </summary>
    /// <summary>Where finished releases ended up, one level above any collection folder they were kept in.</summary>
    public static List<string> OutputParents(RunReport report) =>
        report.Releases.Where(r => r.FinalPath != null && r.State != ReleaseState.Culled)
            .Select(r =>
            {
                var dir = Path.GetDirectoryName(r.FinalPath!.TrimEnd(Path.DirectorySeparatorChar))!;
                if (r.Group.Length > 0 && dir.EndsWith(Path.DirectorySeparatorChar + r.Group, StringComparison.OrdinalIgnoreCase))
                    dir = dir[..^(r.Group.Length + 1)];
                return dir;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static FolderSnapshot TakeMany(string root, IEnumerable<string> otherParents, PressingSettings s)
    {
        var snap = Take(root, s);
        var parents = otherParents.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) && !FileOps.PathsEqual(p, root))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in parents)
        {
            // The parent itself (e.g. "Pressed") shows up as a tile of the root; replace it by what's inside.
            snap.Folders.RemoveAll(f => FileOps.PathsEqual(f.Path, p));
            foreach (var f in Take(p, s).Folders.Where(f => !snap.Folders.Any(x => FileOps.PathsEqual(x.Path, f.Path))))
                snap.Folders.Add(f);
        }
        snap.Folders = snap.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return snap;
    }

    /// <summary>"cue, log, nfo" — the kinds of extra files, most common first.</summary>
    public static string Describe(IEnumerable<string> files) =>
        string.Join(", ", files.Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
            .Select(e => e.Length == 0 ? "no ext" : e).GroupBy(e => e).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key));

    /// <summary>"{album} ({year})" → ^.+ \(\d{4}\)$ so any folder already in your naming format is recognised.</summary>
    public static Regex TemplateRegex(string template)
    {
        var parts = Regex.Split(template ?? "", @"(\{\w+\})");
        var rx = string.Concat(parts.Select(p => p switch
        {
            "{year}" => @"\d{4}",
            _ when p.StartsWith('{') && p.EndsWith('}') => ".+?",
            _ => Regex.Escape(p),
        }));
        return new Regex("^" + rx + "$", RegexOptions.IgnoreCase);
    }

    private static List<string> SafeFiles(string dir, SearchOption opt)
    {
        try { return Directory.EnumerateFiles(dir, "*", opt).ToList(); } catch { return new(); }
    }

    private static List<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); } catch { return new(); }
    }
}
