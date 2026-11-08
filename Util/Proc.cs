using System.Diagnostics;
using System.Text;

namespace Pressing.Core.Util;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class Proc
{
    /// <summary>Runs a process and captures stdout/stderr as text.</summary>
    public static Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default) =>
        RunCoreAsync(exe, args, null, ct);

    /// <summary>Runs a process and hands its raw stdout stream to <paramref name="consumeStdout"/>.</summary>
    public static Task<ProcResult> RunStreamAsync(string exe, IEnumerable<string> args, Func<Stream, CancellationToken, Task> consumeStdout, CancellationToken ct = default) =>
        RunCoreAsync(exe, args, consumeStdout, ct);

    private static async Task<ProcResult> RunCoreAsync(string exe, IEnumerable<string> args, Func<Stream, CancellationToken, Task>? consume, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (consume == null) psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var a in args) psi.ArgumentList.Add(a);

        var tool = Path.GetFileNameWithoutExtension(exe);
        var subject = psi.ArgumentList.LastOrDefault(a => a.Length > 3 && (a.Contains(Path.DirectorySeparatorChar) || a.Contains('/')) && !a.StartsWith('-'));
        var label = subject != null ? Path.GetFileName(subject.TrimEnd(Path.DirectorySeparatorChar)) : string.Join(" ", psi.ArgumentList.Take(3));
        var sw = Stopwatch.StartNew();

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e)
        {
            RunLog.Write("tool", $"{tool} {label} → could not start: {e.Message}");
            return new ProcResult(-1, "", $"Could not start {exe}: {e.Message}");
        }

        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        var errTask = p.StandardError.ReadToEndAsync();
        string stdout = "";
        if (consume != null)
        {
            try { await consume(p.StandardOutput.BaseStream, ct).ConfigureAwait(false); }
            finally { try { await p.StandardOutput.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); } catch { } }
        }
        else stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);

        var stderr = await errTask.ConfigureAwait(false);
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        sw.Stop();
        RunLog.Record("tool:" + tool, $"{tool} {label}", sw.Elapsed);
        RunLog.Write("tool", $"{tool} {label} → exit {p.ExitCode} in {RunLog.Ms(sw.Elapsed)}" +
                             (p.ExitCode != 0 && stderr.Length > 0 ? $" — {stderr.Trim().Split('\n').LastOrDefault()?.Trim()}" : ""));
        ct.ThrowIfCancellationRequested();
        return new ProcResult(p.ExitCode, stdout, stderr);
    }
}
