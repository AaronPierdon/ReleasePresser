using System.Runtime.InteropServices;

namespace Pressing.Core.Pipeline;

/// <summary>Removes the Zone.Identifier stream ("Mark of the Web") that Windows adds to downloaded files.</summary>
public static class Unblocker
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string path);

    public static (int Unblocked, int Total) UnblockTree(string root, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        int unblocked = 0, total = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            total++;
            var full = f.Length >= 248 && !f.StartsWith(@"\\?\") ? @"\\?\" + f : f;
            if (DeleteFileW(full + ":Zone.Identifier")) unblocked++;
        }
        return (unblocked, total);
    }
}
