using Pressing.Core.Models;

namespace Pressing.Core.Util;

public sealed class Toolset
{
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
    public string? Flac { get; init; }
    public string? Fpcalc { get; init; }

    public IEnumerable<string> Missing()
    {
        if (Ffmpeg == null) yield return "ffmpeg (required: decoding, splitting, DR)";
        if (Ffprobe == null) yield return "ffprobe (required: reading audio info)";
        if (Flac == null) yield return "flac (recommended: reference FLAC encoder for splits; ffmpeg is used otherwise)";
        if (Fpcalc == null) yield return "fpcalc (optional: AcoustID fingerprinting)";
    }

    public static Toolset Resolve(PressingSettings s) => new()
    {
        Ffmpeg = ToolLocator.Find("ffmpeg", s.FfmpegPath),
        Ffprobe = ToolLocator.Find("ffprobe", s.FfprobePath),
        Flac = ToolLocator.Find("flac", s.FlacPath),
        Fpcalc = ToolLocator.Find("fpcalc", s.FpcalcPath),
    };
}

public static class ToolLocator
{
    public static string? Find(string name, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", exe),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(PressingSettings.DataDirectory, "tools", exe),
        };
        // winget puts command-line tools here; check them even if PATH in this process is stale.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", exe));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links", exe));
        var pathVar = string.Join(Path.PathSeparator.ToString(),
            Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
        candidates.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim('"'), exe)));

        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null || !OperatingSystem.IsWindows()) return found;

        // Last resort: winget's package folders (e.g. ...\WinGet\Packages\Gyan.FFmpeg_*\ffmpeg-*\bin\ffmpeg.exe)
        foreach (var root in new[]
                 {
                     Path.Combine(local, "Microsoft", "WinGet", "Packages"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var hit = Directory.EnumerateFiles(root, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }
}
