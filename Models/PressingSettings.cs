using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pressing.Core.Models;

public enum YearMode { Original, ThisEdition }
public enum RemoveMode { RecycleBin, MoveToTrashFolder, DeletePermanently }
/// <summary>What to do with an album that turns out to be by a different artist than the folder it's in.</summary>
public enum WrongArtistAction { FlagOnly, MoveToArtistFolder, LeaveUntouched }
/// <summary>Where finished releases go.</summary>
public enum OutputLocation { PressedSubfolder, ReadyFolder, InPlace }

public sealed class PressingSettings
{
    // ---------- Naming ----------
    /// <summary>Tokens: {track} {title} {artist} {albumartist} {album} {year} {disc}</summary>
    public string FileNameTemplate { get; set; } = "{track} - {title}";
    /// <summary>Tokens: {album} {year} {albumartist} {label} {catalog} {quality} {format}</summary>
    public string FolderNameTemplate { get; set; } = "{album} ({year})";
    /// <summary>Release folder name in label / collection folders (artist and catalog matter there).</summary>
    public string CollectionFolderNameTemplate { get; set; } = "[{catalog}] {albumartist} - {album} ({year})";
    /// <summary>Label folders: keep each release inside its collection sub-folder ("13 Hippie Dance") instead of flattening.</summary>
    public bool KeepCollectionFolders { get; set; } = true;
    public int TrackNumberPadding { get; set; } = 2;
    public string DiscFolderPrefix { get; set; } = "CD";
    /// <summary>Joins multiple track artists in the ARTIST tag.</summary>
    public string ArtistSeparator { get; set; } = "; ";
    public string ColonReplacement { get; set; } = " - ";
    public string IllegalCharReplacement { get; set; } = "";
    /// <summary>Your old RenameFolder script wrote "Album (CD1)" into the album tag of multi-disc sets.</summary>
    public bool AppendDiscToAlbumTag { get; set; } = true;

    // ---------- Metadata ----------
    public bool UseMusicBrainz { get; set; } = true;
    public bool UseDiscogs { get; set; } = true;
    public bool UseAcoustId { get; set; } = true;
    public bool UseAi { get; set; } = true;
    public string DiscogsToken { get; set; } = "";
    public string AcoustIdKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-flash-latest";
    /// <summary>Matches at or above this are accepted without trying further sources.</summary>
    public double AcceptConfidence { get; set; } = 0.85;
    /// <summary>Below this a match is rejected outright.</summary>
    public double MinimumConfidence { get; set; } = 0.55;
    public YearMode YearMode { get; set; } = YearMode.Original;
    public bool RemoveComments { get; set; } = false;
    /// <summary>Turn ’ ‘ “ ” from databases into plain ' and \" (Don’t → Don't).</summary>
    public bool StraightQuotes { get; set; } = true;
    /// <summary>Artist folders: every album's ALBUMARTIST becomes the folder's artist; collaborators stay in ARTIST.</summary>
    public bool OneAlbumArtistPerFolder { get; set; } = true;
    public string MusicBrainzContact { get; set; } = "";

    // ---------- Speed ----------
    /// <summary>
    /// How many album folders are analysed at the same time (reading files, splitting, online lookups, DR).
    /// Online services still get their polite pace (MusicBrainz 1/s, Gemini 1 per 6 s); working on several albums
    /// lets one album's lookups overlap another's file work. Applying changes always runs one album at a time.
    /// </summary>
    public int ParallelAlbums { get; set; } = 4;

    // ---------- Splitting ----------
    public int FlacCompressionLevel { get; set; } = 8;
    public bool VerifySplitsBitExact { get; set; } = true;
    /// <summary>Pregap before track 1 longer than this becomes its own "00" hidden track.</summary>
    public double HiddenTrackMinSeconds { get; set; } = 4.0;
    public bool RemoveImagesAfterVerifiedSplit { get; set; } = true;

    // ---------- Dupes ----------
    public bool DupeCullingEnabled { get; set; } = true;
    public double DrMarginDb { get; set; } = 1.0;
    public bool AiTiebreaker { get; set; } = true;
    public bool AutoKeepBestWhenUndecided { get; set; } = false;
    public bool NeverCullVersionWithExtraTracks { get; set; } = true;
    /// <summary>
    /// On (default): only exact copies (same audio, same format) are removed automatically. Different editions get a
    /// suggestion, but stay "keep all" until you pick one on the review screen — so a rare pressing is never removed
    /// without you seeing it.
    /// </summary>
    public bool AutoRemoveOnlyIdentical { get; set; } = true;

    // ---------- Already-sorted folders ----------
    public WrongArtistAction WrongArtistAction { get; set; } = WrongArtistAction.FlagOnly;

    // ---------- Cleanup / output ----------
    public List<string> KeepExtensions { get; set; } = new()
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wv", ".ape", ".wav", ".aiff", ".aif", ".dsf", ".dff",
        ".jpg", ".jpeg", ".png"
    };
    /// <summary>
    /// On (default): a finished release holds only audio + its cover. JPGs in the release folder (and CD folders) stay;
    /// a PNG stays only when there's no JPG (it's the cover). Other subfolders (Artwork, Scans, Extra…) are removed.
    /// </summary>
    public bool KeepOnlyCoverArt { get; set; } = true;
    public RemoveMode RemoveMode { get; set; } = RemoveMode.RecycleBin;
    /// <summary>
    /// PressedSubfolder (default): finished releases move into "Pressed" inside the folder you ran on, so whatever is
    /// left outside still needs you. ReadyFolder: move to <see cref="ReadyFolder"/>\&lt;artist&gt;. InPlace: stay where they are (flattened).
    /// </summary>
    public OutputLocation Output { get; set; } = OutputLocation.PressedSubfolder;
    /// <summary>The "[--]" prefix sorts it to the top in Explorer and makes it obvious it isn't an album.</summary>
    public string PressedFolderName { get; set; } = DefaultPressedFolderName;
    public const string DefaultPressedFolderName = "[--] Pressed";
    /// <summary>The name used before 0.2; still recognised (never scanned, renamed on the next apply).</summary>
    public const string LegacyPressedFolderName = "Pressed";
    public string ReadyFolder { get; set; } = "";
    public bool UnblockFiles { get; set; } = true;

    // ---------- Tools ----------
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string FlacPath { get; set; } = "";
    public string FpcalcPath { get; set; } = "";

    // ---------- Persistence ----------
    [JsonIgnore] public static Func<string, string>? Protect { get; set; }
    [JsonIgnore] public static Func<string, string>? Unprotect { get; set; }

    public static string DataDirectory
    {
        get
        {
            var d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pressing");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string DefaultPath => System.IO.Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PressingSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<PressingSettings>(File.ReadAllText(path), Json) ?? new();
                s.DiscogsToken = Reveal(s.DiscogsToken);
                s.AcoustIdKey = Reveal(s.AcoustIdKey);
                s.GeminiApiKey = Reveal(s.GeminiApiKey);
                if (string.IsNullOrWhiteSpace(s.PressedFolderName) || s.PressedFolderName.Trim() == LegacyPressedFolderName)
                    s.PressedFolderName = DefaultPressedFolderName; // the old default moves to the new name
                return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new PressingSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var copy = (PressingSettings)MemberwiseClone();
        copy.KeepExtensions = new List<string>(KeepExtensions);
        copy.DiscogsToken = Hide(DiscogsToken);
        copy.AcoustIdKey = Hide(AcoustIdKey);
        copy.GeminiApiKey = Hide(GeminiApiKey);
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Json));
    }

    public PressingSettings Clone()
    {
        var c = (PressingSettings)MemberwiseClone();
        c.KeepExtensions = new List<string>(KeepExtensions);
        return c;
    }

    private static string Hide(string v) =>
        string.IsNullOrEmpty(v) || Protect == null ? v : "enc:" + Protect(v);

    private static string Reveal(string v)
    {
        if (string.IsNullOrEmpty(v) || !v.StartsWith("enc:")) return v;
        try { return Unprotect?.Invoke(v[4..]) ?? ""; } catch { return ""; }
    }
}
