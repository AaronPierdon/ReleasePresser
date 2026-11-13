using System.Security.Cryptography;
using System.Text;

namespace Pressing.Core.Cue;

/// <summary>
/// Computes the MusicBrainz Disc ID from a single-file CD image cue. This identifies the exact pressing
/// (track layout down to the frame), which is far more reliable than searching by title.
/// </summary>
public static class DiscId
{
    public static (string Id, string Toc)? FromCue(CueSheet cue, long totalSamples, int sampleRate)
    {
        if (sampleRate != 44100 || cue.Files.Count != 1) return null;
        var tracks = cue.AudioTracks.Where(t => t.Index01.HasValue).OrderBy(t => t.Number).ToList();
        if (tracks.Count == 0 || tracks.Count > 99) return null;
        if (totalSamples % 588 != 0) return null; // not a CD-sector-aligned rip

        int first = tracks[0].Number, last = tracks[^1].Number;
        if (first < 1 || last > 99 || last - first + 1 != tracks.Count) return null;

        long leadout = totalSamples / 588 + 150;
        var offsets = tracks.Select(t => t.Index01!.Value + 150).ToList();

        var sb = new StringBuilder();
        sb.Append(first.ToString("X2")).Append(last.ToString("X2")).Append(leadout.ToString("X8"));
        for (int i = 1; i <= 99; i++)
        {
            int idx = i - first;
            long v = idx >= 0 && idx < offsets.Count ? offsets[idx] : 0;
            sb.Append(v.ToString("X8"));
        }
        var digest = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
        var id = Convert.ToBase64String(digest).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        var toc = $"{first}+{last}+{leadout}+" + string.Join("+", offsets);
        return (id, toc);
    }
}
