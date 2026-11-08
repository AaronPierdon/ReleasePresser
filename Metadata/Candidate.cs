namespace Pressing.Core.Metadata;

/// <summary>A release as described by one online source, normalized so all sources can be scored the same way.</summary>
public sealed class Candidate
{
    public string Source { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Url { get; set; }
    public string Album { get; set; } = "";
    public List<string> AlbumArtists { get; set; } = new();
    public int? Year { get; set; }
    public int? OriginalYear { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string? Barcode { get; set; }
    public string? Country { get; set; }
    public string? Edition { get; set; }
    public bool IsCompilation { get; set; }
    public List<CandidateMedium> Media { get; set; } = new();
    public double Score { get; set; }
    public string Why { get; set; } = "";

    public IEnumerable<CandidateTrack> AllTracks => Media.SelectMany(m => m.Tracks);
    public int TrackCount => Media.Sum(m => m.Tracks.Count);
}

public sealed class CandidateMedium
{
    public int Position { get; set; } = 1;
    public string? Format { get; set; }
    public List<CandidateTrack> Tracks { get; set; } = new();
}

public sealed class CandidateTrack
{
    public int Disc { get; set; } = 1;
    public int Position { get; set; }
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public double? Length { get; set; }
    public string? Isrc { get; set; }
    /// <summary>Set by the AI source: which local file this track belongs to.</summary>
    public string? FileName { get; set; }
}
