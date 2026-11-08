using Pressing.Core.Audio;
using Pressing.Core.Models;

namespace Pressing.Core.Util;

/// <summary>
/// Decides which files in a release folder are "extra" (removed by cleanup). Used by the plan, by cleanup and by the
/// Before/After snapshots, so all three always agree. Audio is never extra: music is only ever removed by an
/// explicit duplicate/redundant decision, never by cleanup.
/// </summary>
public sealed class KeepPolicy
{
    private readonly PressingSettings _s;
    private readonly HashSet<string> _keepExt;

    public KeepPolicy(PressingSettings s)
    {
        _s = s;
        _keepExt = new HashSet<string>(s.KeepExtensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsJpg(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
    public static bool IsPng(string f) => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    public static bool IsDiscFolder(string name) => TextUtil.DiscFolderPattern.IsMatch(name);

    /// <summary>
    /// Extra files under <paramref name="releaseDir"/>. Folders listed in <paramref name="skipDirs"/> (other releases nested
    /// inside this one) are left completely alone.
    /// </summary>
    public List<string> Extras(string releaseDir, IEnumerable<string>? skipDirs = null)
    {
        var result = new List<string>();
        if (!Directory.Exists(releaseDir)) return result;
        var skip = (skipDirs ?? Enumerable.Empty<string>())
            .Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Where(d => !FileOps.PathsEqual(d, releaseDir)).ToList();

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(releaseDir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return result; }

        var byFolder = files
            .Where(f => !skip.Any(s => f.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains("_pressing_tmp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in byFolder)
        {
            bool releaseLevel = FileOps.PathsEqual(folder.Key, releaseDir)
                                || (IsDiscFolder(Path.GetFileName(folder.Key)) && FileOps.PathsEqual(Path.GetDirectoryName(folder.Key)!, releaseDir));
            var list = folder.ToList();
            if (!_s.KeepOnlyCoverArt)
            {
                result.AddRange(list.Where(f => !Formats.IsAudio(f) && !_keepExt.Contains(Path.GetExtension(f))));
                continue;
            }
            // A png is the cover only when there's no jpg next to it; keep the best-named one.
            string? pngCover = null;
            if (releaseLevel && !list.Any(IsJpg))
                pngCover = list.Where(IsPng)
                    .OrderByDescending(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "cover|front|folder", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .ThenBy(f => Path.GetFileName(f).Length).FirstOrDefault();
            foreach (var f in list)
            {
                if (Formats.IsAudio(f)) continue;
                if (releaseLevel && (IsJpg(f) || f == pngCover)) continue;
                result.Add(f);
            }
        }
        return result;
    }
}
