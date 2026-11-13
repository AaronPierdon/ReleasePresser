namespace Pressing.Core.Models;

/// <summary>
/// A picture of an artist/label folder at one moment: every folder directly inside it, what's in each,
/// and any loose files. Taken from the disk only (no tags, no lookups), so it's quick and it's the truth.
/// The Before page shows the one taken before analysis; the After page shows a fresh one taken after Apply.
/// </summary>
public sealed class FolderSnapshot
{
    public string Root { get; set; } = "";
    public DateTime TakenAt { get; set; } = DateTime.Now;
    public List<FolderTile> Folders { get; set; } = new();
    /// <summary>Files sitting directly in the artist/label folder (not inside any release folder).</summary>
    public List<string> LooseFiles { get; set; } = new();
    /// <summary>Albums already in the "[--] Pressed" output folder (not shown as tiles).</summary>
    public int PressedAlready { get; set; }

    public int AudioFiles => Folders.Sum(f => f.AudioFiles);
    public long AudioBytes => Folders.Sum(f => f.AudioBytes);
    public int ExtraFiles => Folders.Sum(f => f.ExtraFiles.Count);
    public int CleanCount => Folders.Count(f => f.LooksPressed);
}

/// <summary>One folder directly inside the root, as found on disk.</summary>
public sealed class FolderTile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Collection folder it sits in ("13 Hippie Dance"), "" when directly in the root.</summary>
    public string Group { get; set; } = "";
    public int AudioFiles { get; set; }
    public long AudioBytes { get; set; }
    /// <summary>"FLAC" → 12, "MP3" → 3 …</summary>
    public Dictionary<string, int> AudioByFormat { get; set; } = new();
    public int Jpgs { get; set; }
    public int Pngs { get; set; }
    public int Cues { get; set; }
    /// <summary>Anything that isn't audio or a cover image (cue, log, nfo, m3u, sfv, exe, other images in subfolders…). Relative paths.</summary>
    public List<string> ExtraFiles { get; set; } = new();
    /// <summary>CD1 / Disc 2 style subfolders.</summary>
    public List<string> DiscFolders { get; set; } = new();
    /// <summary>Any other subfolder (Artwork, Scans, Extra, Tech_Info …).</summary>
    public List<string> OtherFolders { get; set; } = new();
    /// <summary>Name looks like "Album (Year)", only audio + cover art, no stray subfolders.</summary>
    public bool LooksPressed { get; set; }
    /// <summary>One plain-English reason it doesn't look pressed yet (empty when it does).</summary>
    public string Problem { get; set; } = "";
}

/// <summary>Result of the "nothing lost" check that runs before and after Apply.</summary>
public sealed class SafetyAudit
{
    /// <summary>Every recording (track) the analysis found.</summary>
    public int Recordings { get; set; }
    /// <summary>Still in place in a folder you kept.</summary>
    public int InPlace { get; set; }
    /// <summary>In a removed folder, but the same recording lives on in a kept folder.</summary>
    public int CoveredElsewhere { get; set; }
    /// <summary>Gone with no copy anywhere. Must be empty; anything here is a bug or a manual change.</summary>
    public List<string> Lost { get; set; } = new();
    /// <summary>Removals the safety net refused because they held unique recordings.</summary>
    public List<string> Blocked { get; set; } = new();
    public bool Ok => Lost.Count == 0;
}
