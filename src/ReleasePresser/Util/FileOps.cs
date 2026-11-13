using System.Runtime.InteropServices;
using System.Text.Json;
using Pressing.Core.Models;

namespace Pressing.Core.Util;

public sealed record JournalEntry(string Op, string From, string? To, DateTime At);

/// <summary>
/// Every rename, move and removal goes through here so it is written to an append-only journal.
/// Renames and moves can be undone from the journal; removals go to the Recycle Bin by default.
/// </summary>
public sealed class FileOps
{
    private readonly PressingSettings _s;
    private readonly string _journalPath;
    private readonly string _trashRoot;
    private readonly object _lock = new();

    public FileOps(PressingSettings s, string journalPath, string trashRoot)
    {
        _s = s;
        _journalPath = journalPath;
        _trashRoot = trashRoot;
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
    }

    public string JournalPath => _journalPath;

    private void Log(string op, string from, string? to)
    {
        RunLog.Write("file", to == null ? $"{op}: {from}" : $"{op}: {from}  →  {to}");
        var line = JsonSerializer.Serialize(new JournalEntry(op, from, to, DateTime.Now));
        lock (_lock) File.AppendAllText(_journalPath, line + Environment.NewLine);
    }

    public string RenameFile(string from, string to)
    {
        if (PathsEqual(from, to)) return from;
        to = Unique(to, isDir: false, ignore: from);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = from + ".pressing-tmp";
            File.Move(from, tmp);
            File.Move(tmp, to);
        }
        else File.Move(from, to);
        Log("move-file", from, to);
        return to;
    }

    public string MoveDirectory(string from, string to)
    {
        if (PathsEqual(from, to)) return from;
        to = Unique(to, isDir: true, ignore: from);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = from.TrimEnd(Path.DirectorySeparatorChar) + ".pressing-tmp";
            Directory.Move(from, tmp);
            Directory.Move(tmp, to);
        }
        else if (SameVolume(from, to)) Directory.Move(from, to);
        else
        {
            CopyDirectory(from, to);
            Directory.Delete(from, true);
        }
        Log("move-dir", from, to);
        return to;
    }

    public void Remove(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        switch (_s.RemoveMode)
        {
            // Network shares have no Recycle Bin (Windows would delete for good), so they use the trash folder instead.
            case RemoveMode.RecycleBin when OperatingSystem.IsWindows() && !IsNetworkPath(path):
                RecycleBin.Send(path);
                Log("recycle", path, null);
                break;
            case RemoveMode.DeletePermanently:
                if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
                Log("delete", path, null);
                break;
            default:
                var dest = Path.Combine(_trashRoot, DateTime.Now.ToString("yyyyMMdd"), Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                dest = Unique(dest, Directory.Exists(path), null);
                if (Directory.Exists(path))
                {
                    if (SameVolume(path, dest)) Directory.Move(path, dest);
                    else { CopyDirectory(path, dest); Directory.Delete(path, true); }
                }
                else File.Move(path, dest);
                Log("trash", path, dest);
                break;
        }
    }

    public void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }

    // ---------------- Undo ----------------

    /// <summary>Reverses moves/renames in a journal, newest first. Removed files must be restored from the Recycle Bin.</summary>
    public static (int Undone, List<string> Problems) Undo(string journalPath)
    {
        var problems = new List<string>();
        int undone = 0;
        var entries = File.ReadAllLines(journalPath)
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<JournalEntry>(l)!)
            .Reverse().ToList();
        foreach (var e in entries)
        {
            try
            {
                switch (e.Op)
                {
                    case "move-file" when File.Exists(e.To):
                        Directory.CreateDirectory(Path.GetDirectoryName(e.From)!);
                        File.Move(e.To!, e.From); undone++; break;
                    case "move-dir" when Directory.Exists(e.To):
                        Directory.CreateDirectory(Path.GetDirectoryName(e.From)!);
                        Directory.Move(e.To!, e.From); undone++; break;
                    case "trash" when e.To != null && (File.Exists(e.To) || Directory.Exists(e.To)):
                        if (Directory.Exists(e.To)) Directory.Move(e.To, e.From); else File.Move(e.To, e.From);
                        undone++; break;
                    case "recycle":
                    case "delete":
                        problems.Add($"Not restored automatically ({e.Op}): {e.From}");
                        break;
                }
            }
            catch (Exception ex) { problems.Add($"{e.Op} {e.From}: {ex.Message}"); }
        }
        return (undone, problems);
    }

    // ---------------- Helpers ----------------

    public static bool IsNetworkPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\")) return true; // UNC: \\server\share
        try { return new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network; }
        catch { return false; }
    }

    public static string Unique(string path, bool isDir, string? ignore)
    {
        bool Exists(string p) => (isDir ? Directory.Exists(p) : File.Exists(p)) && (ignore == null || !PathsEqual(p, ignore) && !string.Equals(p, ignore, StringComparison.OrdinalIgnoreCase));
        if (!Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(p)) return p;
        }
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    public static void Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0) throw new IOException($"Recycle Bin failed for {path} (code {rc})");
    }
}
