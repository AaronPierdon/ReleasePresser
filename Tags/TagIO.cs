using Pressing.Core.Models;
using TagLib;
using File = TagLib.File;

namespace Pressing.Core.Tags;

public static class TagIO
{
    public static TagSnapshot Read(string path)
    {
        var s = new TagSnapshot();
        try
        {
            using var f = File.Create(path);
            var t = f.Tag;
            s.Title = Nz(t.Title);
            s.Artist = Nz(t.JoinedPerformers);
            s.AlbumArtist = Nz(t.JoinedAlbumArtists);
            s.Album = Nz(t.Album);
            s.Year = t.Year > 0 ? (int)t.Year : null;
            s.Track = t.Track > 0 ? (int)t.Track : null;
            s.TrackTotal = t.TrackCount > 0 ? (int)t.TrackCount : null;
            s.Disc = t.Disc > 0 ? (int)t.Disc : null;
            s.DiscTotal = t.DiscCount > 0 ? (int)t.DiscCount : null;
            s.Genre = Nz(t.JoinedGenres);
            s.Label = Nz(t.Publisher) ?? Custom(f, "LABEL") ?? Custom(f, "ORGANIZATION");
            s.CatalogNumber = Nz(Custom(f, "CATALOGNUMBER") ?? Custom(f, "CATALOG"));
            s.Barcode = Nz(Custom(f, "BARCODE") ?? Custom(f, "UPC"));
            s.MusicBrainzReleaseId = Nz(t.MusicBrainzReleaseId);
            s.Isrc = Nz(t.ISRC);
            if (s.Year == null)
            {
                var d = Custom(f, "DATE") ?? Custom(f, "ORIGINALDATE") ?? Custom(f, "YEAR");
                s.Year = Util.TextUtil.ParseYear(d);
            }
        }
        catch { }
        return s;
    }

    /// <summary>Returns a cue sheet embedded in a FLAC/APE/WavPack image's tags, if any.</summary>
    public static string? ReadEmbeddedCue(string path)
    {
        try
        {
            using var f = File.Create(path);
            return Custom(f, "CUESHEET");
        }
        catch { return null; }
    }

    public static void Write(string path, TagSnapshot v, PressingSettings s, string? albumOverride = null)
    {
        using var f = File.Create(path);
        var t = f.Tag;
        if (f is TagLib.Mpeg.AudioFile) f.GetTag(TagTypes.Id3v2, true);
        if (f is TagLib.Ape.File || f is TagLib.WavPack.File) f.GetTag(TagTypes.Ape, true);

        t.Title = v.Title;
        t.Performers = string.IsNullOrWhiteSpace(v.Artist) ? Array.Empty<string>() : new[] { v.Artist };
        t.AlbumArtists = string.IsNullOrWhiteSpace(v.AlbumArtist) ? Array.Empty<string>() : new[] { v.AlbumArtist };
        t.Album = albumOverride ?? v.Album;
        t.Year = (uint)(v.Year ?? 0);
        // A stray YEAR field next to DATE shows up as two years ("2010\\2008") in taggers: keep just the one we write.
        if (v.Year is > 0 && f.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xc) xc.RemoveField("YEAR");
        t.Track = (uint)(v.Track ?? 0);
        t.TrackCount = (uint)(v.TrackTotal ?? 0);
        t.Disc = (uint)(v.Disc ?? 0);
        t.DiscCount = (uint)(v.DiscTotal ?? 0);
        if (!string.IsNullOrWhiteSpace(v.Genre)) t.Genres = new[] { v.Genre };
        if (!string.IsNullOrWhiteSpace(v.Label)) t.Publisher = v.Label;
        if (!string.IsNullOrWhiteSpace(v.MusicBrainzReleaseId)) t.MusicBrainzReleaseId = v.MusicBrainzReleaseId;
        if (!string.IsNullOrWhiteSpace(v.Isrc)) t.ISRC = v.Isrc;
        if (s.RemoveComments) t.Comment = null;

        SetCustom(f, "LABEL", v.Label);
        SetCustom(f, "CATALOGNUMBER", v.CatalogNumber);
        SetCustom(f, "BARCODE", v.Barcode);
        // Strip stale cue sheets from split tracks so players don't treat them as images.
        SetCustom(f, "CUESHEET", null, removeOnly: true);
        f.Save();
    }

    // ------------------------------------------------------------ custom fields

    private static string? Custom(File f, string key)
    {
        if (f.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment x)
        {
            var v = x.GetFirstField(key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        if (f.GetTag(TagTypes.Ape) is TagLib.Ape.Tag a)
        {
            var item = a.GetItem(key);
            if (item != null && !string.IsNullOrWhiteSpace(item.ToString())) return item.ToString();
        }
        if (f.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            var fr = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
            if (fr != null && fr.Text.Length > 0 && !string.IsNullOrWhiteSpace(fr.Text[0])) return fr.Text[0];
        }
        if (f.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            var v = apple.GetDashBox("com.apple.iTunes", key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static void SetCustom(File f, string key, string? value, bool removeOnly = false)
    {
        if (!removeOnly && string.IsNullOrWhiteSpace(value)) return;
        if (f.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment x)
        {
            if (removeOnly) x.RemoveField(key); else x.SetField(key, value!);
        }
        if (f.GetTag(TagTypes.Ape) is TagLib.Ape.Tag a)
        {
            if (removeOnly) a.RemoveItem(key); else a.SetValue(key, value!);
        }
        if (f.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            if (removeOnly)
            {
                var fr = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
                if (fr != null) id3.RemoveFrame(fr);
            }
            else TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true).Text = new[] { value! };
        }
        if (!removeOnly && f.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            apple.SetDashBox("com.apple.iTunes", key, value!);
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
