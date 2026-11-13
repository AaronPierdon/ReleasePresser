using Pressing.Core.Models;

namespace Pressing.Core.Util;

/// <summary>Installs the external audio tools with winget (built into Windows 10/11 and Server 2025).</summary>
public static class ToolInstaller
{
    public static readonly (string Tool, string WingetId)[] Packages =
    {
        ("ffmpeg + ffprobe", "Gyan.FFmpeg"),
        ("flac", "Xiph.FLAC"),
    };

    public static string? FindWinget() => ToolLocator.Find("winget", null);

    /// <summary>Installs whatever is missing. Returns a human-readable result per package.</summary>
    public static async Task<List<string>> InstallMissingAsync(PressingSettings s, Action<string> log, CancellationToken ct)
    {
        var results = new List<string>();
        var winget = FindWinget();
        if (winget == null)
        {
            results.Add("winget isn't available on this machine. Install \"App Installer\" from the Microsoft Store, or download ffmpeg and flac and set their paths in Settings → Tools.");
            return results;
        }

        var tools = Toolset.Resolve(s);
        var needed = new List<(string Tool, string Id)>();
        if (tools.Ffmpeg == null || tools.Ffprobe == null) needed.Add(Packages[0]);
        if (tools.Flac == null) needed.Add(Packages[1]);

        foreach (var (tool, id) in needed)
        {
            log($"Installing {tool} ({id})…");
            var r = await Proc.RunAsync(winget, new[]
            {
                "install", "--id", id, "-e", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
            }, ct);
            var tail = (r.StdOut + r.StdErr).Trim();
            if (tail.Length > 400) tail = tail[^400..];
            results.Add(r.Ok ? $"{tool}: installed." : $"{tool}: winget exit code {r.ExitCode}. {tail}");
            log(results[^1]);
        }

        var after = Toolset.Resolve(s);
        if (after.Ffmpeg != null && after.Ffprobe != null && after.Flac != null) results.Add("All tools found.");
        else results.Add("Still missing: " + string.Join(", ", after.Missing().Where(m => !m.StartsWith("fpcalc")).Select(m => m.Split(' ')[0])) +
                         ". If they were just installed, restart Pressing so Windows refreshes its tool paths.");
        return results;
    }
}
