using System.Text.Json.Serialization;

namespace Pressing.Core.Models;

public enum Severity { Info, Warning, Error }

public enum ReleaseState { Pending, Running, Done, Flagged, Failed, Culled }

public enum LibraryMode { Auto, Artist, Collection }

public static class Stages
{
    public const string Unblock = "Unblock";
    public const string Scan = "Scan";
    public const string Split = "Split";
    public const string Metadata = "Metadata";
    public const string Dupes = "Dupes";
    public const string Rename = "Rename";
    public const string Cleanup = "Cleanup";
    public const string Place = "Place";

    public static readonly string[] All = { Unblock, Scan, Split, Metadata, Dupes, Rename, Cleanup, Place };
}

public sealed record Flag(Severity Severity, string Stage, string Message);

/// <summary>Tag values for one track at a point in time (before / after).</summary>
public sealed class TagSnapshot
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public int? Year { get; set; }
    public int? Track { get; set; }
    public int? TrackTotal { get; set; }
    public int? Disc { get; set; }
    public int? DiscTotal { get; set; }
    public string? Genre { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string? Barcode { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? Isrc { get; set; }

    public TagSnapshot Clone() => (TagSnapshot)MemberwiseClone();
}

public sealed class TrackFile
{
    public string Path { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public int Disc { get; set; } = 1;
    public int Number { get; set; }
    public double Duration { get; set; }
    public string Codec { get; set; } = "";
    public bool Lossless { get; set; }
    public int SampleRate { get; set; }
    public int BitDepth { get; set; }
    public int BitrateKbps { get; set; }
    public bool IsBonus { get; set; }
    public double? DynamicRange { get; set; }
    public TagSnapshot Before { get; set; } = new();
    public TagSnapshot After { get; set; } = new();
    /// <summary>True once Pressing has written After into the file (used by Undo).</summary>
    public bool TagsWritten { get; set; }
    /// <summary>The exact ALBUM tag that will be written when it differs from After.Album (e.g. "Album (CD1)").</summary>
    public string? AlbumTag { get; set; }

    [JsonIgnore] public bool TagsDiffer =>
        !string.Equals(Before.Title, After.Title) || !string.Equals(Before.Artist, After.Artist) ||
        !string.Equals(Before.AlbumArtist, After.AlbumArtist) || !string.Equals(Before.Album, AlbumTag ?? After.Album) ||
        Before.Year != After.Year || Before.Track != After.Track || Before.Disc != After.Disc ||
        Before.TrackTotal != After.TrackTotal || Before.DiscTotal != After.DiscTotal ||
        (After.Label != null && Before.Label != After.Label) || (After.CatalogNumber != null && Before.CatalogNumber != After.CatalogNumber);
}

/// <summary>A rename Pressing intends to do (shown on the review screen).</summary>
public sealed record PlannedRename(string Kind, string From, string To);

/// <summary>Another release that already contains a track.</summary>
public sealed class Containment
{
    public string ReleaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Tracks { get; set; }
}

public sealed class DiscFolder
{
    public string Path { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public int Number { get; set; } = 1;
    /// <summary>True when this is a CD1/Disc 2 style subfolder of the release folder.</summary>
    public bool IsSubfolder { get; set; }
    public List<TrackFile> Tracks { get; set; } = new();
}

public sealed class MetadataMatch
{
    public string Source { get; set; } = "";
    public string? Url { get; set; }
    public double Confidence { get; set; }
    public string? Edition { get; set; }
    public string? Notes { get; set; }
    public int MatchedTracks { get; set; }
    public int BonusTracks { get; set; }
}

public sealed class Release
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Path { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    /// <summary>
    /// The collection the release sat in, relative to the root (e.g. "13 Hippie Dance"); "" when directly in the root.
    /// Label folders keep this grouping in the output and on the review screen.
    /// </summary>
    public string Group { get; set; } = "";
    /// <summary>Catalog number from the original folder name ("[HIPPIE DANCE 01] …"), kept for label folder names.</summary>
    public string? FolderCatalog { get; set; }
    public List<DiscFolder> Discs { get; set; } = new();
    public List<Flag> Flags { get; set; } = new();
    public ReleaseState State { get; set; } = ReleaseState.Pending;
    public string Stage { get; set; } = "";
    public double Progress { get; set; }

    /// <summary>Raw text of every cue, log and nfo seen; used as context for lookups.</summary>
    public List<string> ContextDocs { get; set; } = new();
    public List<string> SplitNotes { get; set; } = new();
    public bool WasSplit { get; set; }
    public string? MusicBrainzDiscId { get; set; }
    public string? CdToc { get; set; }

    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public int? Year { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public bool IsCompilation { get; set; }
    public MetadataMatch? Match { get; set; }

    public double? DynamicRange { get; set; }
    public string? DupeGroup { get; set; }
    public string? DupeDecision { get; set; }

    public string? FinalPath { get; set; }
    /// <summary>Set when the album belongs to another artist and should move to that artist's folder.</summary>
    public string? MoveToArtist { get; set; }
    /// <summary>True when the run decided not to change anything in this release.</summary>
    public bool Untouched { get; set; }

    // ---------- Plan (filled by analysis, acted on only after you confirm) ----------
    /// <summary>Apply this release's planned changes. You can untick it on the review screen.</summary>
    public bool Include { get; set; } = true;
    public List<PlannedRename> PlannedRenames { get; set; } = new();
    public string? PlannedFolderName { get; set; }
    /// <summary>The folder name before any clash suffix was added.</summary>
    public string? BaseFolderName { get; set; }
    /// <summary>Files that would be removed by cleanup (junk, leftover cue/images).</summary>
    public List<string> PlannedJunk { get; set; } = new();
    /// <summary>Cue images / cues that become redundant once you confirm the split.</summary>
    public List<string> PendingImageRemovals { get; set; } = new();
    /// <summary>Tracks created by splitting during analysis; deleted again if you discard the plan.</summary>
    public List<string> SplitOutputs { get; set; } = new();
    /// <summary>Every track of this release also exists on these releases.</summary>
    public List<Containment> ContainedIn { get; set; } = new();
    /// <summary>All tracks exist elsewhere — candidate for removal (you decide).</summary>
    public bool Redundant { get; set; }
    public bool RemoveAsRedundant { get; set; }
    public bool TagsComplete { get; set; }
    /// <summary>Already pressed by an earlier run (name, files, tags and contents all clean): nothing to do.</summary>
    public bool AlreadyPressed { get; set; }
    /// <summary>Set by the safety net when a planned removal was refused because it holds recordings found nowhere else.</summary>
    public string? Protected { get; set; }
    public int TagChangeCount => AllTracks.Count(t => t.TagsDiffer);

    [JsonIgnore] public IEnumerable<TrackFile> AllTracks => Discs.SelectMany(d => d.Tracks);

    public bool IsLossless => AllTracks.Any() && AllTracks.All(t => t.Lossless);
    public int MaxBitDepth => AllTracks.Select(t => t.BitDepth).DefaultIfEmpty(0).Max();
    public int MaxSampleRate => AllTracks.Select(t => t.SampleRate).DefaultIfEmpty(0).Max();
    public string FormatSummary
    {
        get
        {
            var codecs = AllTracks.Select(t => t.Codec.ToUpperInvariant()).Distinct().ToList();
            var c = codecs.Count == 0 ? "?" : string.Join("/", codecs);
            if (IsLossless && MaxBitDepth > 0) return $"{c} {MaxBitDepth}/{MaxSampleRate / 1000.0:0.#}";
            var br = AllTracks.Select(t => t.BitrateKbps).DefaultIfEmpty(0).Max();
            return br > 0 ? $"{c} {br}k" : c;
        }
    }

    public void AddFlag(Severity s, string stage, string msg)
    {
        lock (Flags) Flags.Add(new Flag(s, stage, msg));
    }
}

public sealed class DupeVersion
{
    public string ReleaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Format { get; set; } = "";
    public double? DynamicRange { get; set; }
    public int TrackCount { get; set; }
    public string? Edition { get; set; }
    public bool Keep { get; set; }
}

public sealed class DupeGroup
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public List<DupeVersion> Versions { get; set; } = new();
    /// <summary>"dr", "ai", "fallback", "manual", or "undecided".</summary>
    public string DecidedBy { get; set; } = "undecided";
    /// <summary>Release to keep (recommended by analysis, changeable by you). Null = keep all.</summary>
    public string? KeepReleaseId { get; set; }
    public string? RecommendedReleaseId { get; set; }
    public string Reason { get; set; } = "";
    public bool Resolved { get; set; }
}

public sealed class RunReport
{
    /// <summary>2 = plan/confirm runs. 0 = runs from the first version (tags were written during analysis).</summary>
    public int FormatVersion { get; set; }
    /// <summary>"planned" (waiting for your confirmation), "applied", "discarded".</summary>
    public string Phase { get; set; } = "planned";
    public bool ApplyTags { get; set; } = true;
    public bool ApplyRenames { get; set; } = true;
    public bool ApplyCleanup { get; set; } = true;
    public bool ApplyPlace { get; set; } = true;
    public bool KeepInPlace { get; set; }
    public string RunId { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public string Root { get; set; } = "";
    public LibraryMode Mode { get; set; }
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public bool Cancelled { get; set; }
    public List<Release> Releases { get; set; } = new();
    public List<DupeGroup> DupeGroups { get; set; } = new();
    public string? JournalPath { get; set; }
    public List<string> GlobalNotes { get; set; } = new();
    /// <summary>The folder as it was before analysis (disk only).</summary>
    public FolderSnapshot? Before { get; set; }
    /// <summary>The folder as it is after Apply (fresh look at the disk).</summary>
    public FolderSnapshot? After { get; set; }
    /// <summary>"Nothing lost" check: preview before Apply, final result after.</summary>
    public SafetyAudit? Audit { get; set; }
    /// <summary>Folders you chose to skip on the Before page.</summary>
    public List<string> Skipped { get; set; } = new();

    public int CountSplit => Releases.Count(r => r.WasSplit);
    public int CountFlagged => Releases.Count(r => r.Flags.Any(f => f.Severity != Severity.Info));
    public int CountCulled => Releases.Count(r => r.State == ReleaseState.Culled);
    public int CountBySource(string source) => Releases.Count(r => r.Match?.Source == source);
}
