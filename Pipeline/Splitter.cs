using System.Globalization;
using Pressing.Core.Audio;
using Pressing.Core.Cue;
using Pressing.Core.Models;
using Pressing.Core.Tags;
using Pressing.Core.Util;

namespace Pressing.Core.Pipeline;

public sealed class SplitOutcome
{
    public bool Success { get; set; }
    public List<TrackFile> Tracks { get; } = new();
    public string Summary { get; set; } = "";
    public string? DiscId { get; set; }
    public string? Toc { get; set; }
    /// <summary>Image/cue files that can go once you confirm (when removal is deferred).</summary>
    public List<string> ToRemove { get; } = new();
}

/// <summary>
/// Splits cue images into tracks.
/// Lossless images: decoded and re-encoded as FLAC with the reference encoder at the original bit depth and
/// sample rate, then verified by hashing the decoded PCM of the image against the concatenated tracks.
/// Lossy images (MP3 etc.): cut without re-encoding.
/// </summary>
public sealed class Splitter
{
    private readonly Toolset _tools;
    private readonly PressingSettings _s;
    private readonly FileOps _ops;
    private readonly Action<string> _log;

    public Splitter(Toolset tools, PressingSettings s, FileOps ops, Action<string> log)
    {
        _tools = tools; _s = s; _ops = ops; _log = log;
    }

    /// <summary>When true the image and cue are kept after a verified split and listed in <see cref="SplitOutcome.ToRemove"/>.</summary>
    public bool DeferRemoval { get; set; }

    private sealed record Segment(int Number, string? Title, CueTrack? Track, CueFile File, string Image, long StartSample, long? EndSample, bool Hidden);

    public async Task<SplitOutcome> SplitAsync(SplitJob job, Release rel, IProgress<double>? progress, CancellationToken ct)
    {
        var outcome = new SplitOutcome();
        if (_tools.Ffmpeg == null || _tools.Ffprobe == null)
        {
            rel.AddFlag(Severity.Error, Stages.Split, "ffmpeg/ffprobe not found; cannot split.");
            return outcome;
        }

        var infos = new Dictionary<string, AudioInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in job.Images.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = await AudioProbe.ProbeAsync(_tools.Ffprobe, img, ct);
            if (info == null)
            {
                rel.AddFlag(Severity.Error, Stages.Split, $"Can't read image {Path.GetFileName(img)} (corrupt or unsupported).");
                return outcome;
            }
            infos[img] = info;
        }

        bool lossless = infos.Values.All(i => i.Lossless);
        if (!lossless && infos.Values.Any(i => i.Lossless))
        {
            rel.AddFlag(Severity.Error, Stages.Split, "Cue mixes lossless and lossy files; left alone.");
            return outcome;
        }
        if (infos.Values.Any(i => i.IsFloat))
        {
            rel.AddFlag(Severity.Error, Stages.Split, "Image is 32-bit float PCM; refusing to split to avoid any conversion. Left alone.");
            return outcome;
        }

        Directory.CreateDirectory(job.OutputFolder);
        var tmp = Path.Combine(job.Folder, "_pressing_tmp_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var created = new List<string>();
        try
        {
            var segments = new List<Segment>();
            foreach (var cf in job.Cue.Files.Where(f => job.Images.ContainsKey(f)))
            {
                var img = job.Images[cf];
                var info = infos[img];
                var tracks = cf.Tracks.Where(t => t.IsAudio).OrderBy(t => t.Number).ToList();
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    if (t.Index01 == null)
                    {
                        rel.AddFlag(Severity.Error, Stages.Split, $"Track {t.Number} has no INDEX 01 in the cue; left alone.");
                        return outcome;
                    }
                    long start = CueSheet.FramesToSamples(t.Index01.Value, info.SampleRate);
                    long? end = i + 1 < tracks.Count ? CueSheet.FramesToSamples(tracks[i + 1].Index01 ?? 0, info.SampleRate) : null;

                    if (i == 0 && start > 0)
                    {
                        double pregapSec = start / (double)info.SampleRate;
                        bool firstFile = cf == job.Cue.Files.First(f => job.Images.ContainsKey(f));
                        if (firstFile && t.Number == 1 && pregapSec >= _s.HiddenTrackMinSeconds)
                        {
                            segments.Add(new Segment(0, "Hidden Track", null, cf, img, 0, start, true));
                            rel.AddFlag(Severity.Info, Stages.Split, $"Found {pregapSec:0.0}s of audio before track 1 (hidden track); saved as track 00.");
                        }
                        else start = 0; // short pregap: keep it on the front of the first track so nothing is lost
                    }
                    segments.Add(new Segment(t.Number, t.Title, t, cf, img, start, end, false));
                }
            }
            if (segments.Count < 2)
            {
                rel.AddFlag(Severity.Warning, Stages.Split, "Cue lists fewer than two tracks; nothing to split.");
                return outcome;
            }

            // Sanity: every start must be inside the file.
            foreach (var seg in segments)
            {
                var info = infos[seg.Image];
                var total = info.TotalSamples ?? (long)(info.Duration * info.SampleRate);
                if (seg.StartSample >= total && total > 0)
                {
                    rel.AddFlag(Severity.Error, Stages.Split, $"Cue track {seg.Number} starts after the end of {Path.GetFileName(seg.Image)} — wrong cue for this file. Left alone.");
                    return outcome;
                }
            }

            int done = 0;
            if (lossless)
            {
                // Inputs the flac CLI reads directly; anything else is decoded once to a temporary WAV.
                var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var img in infos.Keys)
                {
                    var ext = Path.GetExtension(img).ToLowerInvariant();
                    var info = infos[img];
                    bool direct = _tools.Flac != null && (ext == ".flac" || (ext == ".wav" && info.Codec is "pcm_s16le" or "pcm_s24le"));
                    if (direct) { sources[img] = img; continue; }
                    var wav = Path.Combine(tmp, Path.GetFileNameWithoutExtension(img) + ".wav");
                    var r = await Proc.RunAsync(_tools.Ffmpeg, new[]
                    {
                        "-v", "error", "-nostdin", "-y", "-i", img, "-map", "0:a:0",
                        "-c:a", "pcm_" + PcmTools.PcmFormatFor(info.BitDepth), "-rf64", "auto", wav
                    }, ct);
                    if (!r.Ok) { rel.AddFlag(Severity.Error, Stages.Split, $"Decoding {Path.GetFileName(img)} failed: {Short(r.StdErr)}"); return outcome; }
                    sources[img] = wav;
                }

                var outputs = new string[segments.Count];
                var errors = new List<string>();
                int dop = Math.Max(1, Environment.ProcessorCount / 2);
                await Parallel.ForEachAsync(Enumerable.Range(0, segments.Count), new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct }, async (i, token) =>
                {
                    var seg = segments[i];
                    var info = infos[seg.Image];
                    var outPath = Path.Combine(job.OutputFolder, TrackFileName(seg, ".flac"));
                    outputs[i] = outPath;
                    ProcResult r;
                    if (_tools.Flac != null)
                    {
                        var args = new List<string> { "-" + Math.Clamp(_s.FlacCompressionLevel, 0, 8), "--silent", "--force", "--no-padding", "--padding=8192", $"--skip={seg.StartSample}" };
                        if (seg.EndSample.HasValue) args.Add($"--until={seg.EndSample.Value}");
                        args.AddRange(new[] { "-o", outPath, sources[seg.Image] });
                        r = await Proc.RunAsync(_tools.Flac, args, token);
                    }
                    else
                    {
                        var trim = seg.EndSample.HasValue
                            ? $"atrim=start_sample={seg.StartSample}:end_sample={seg.EndSample.Value}"
                            : $"atrim=start_sample={seg.StartSample}";
                        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-i", sources[seg.Image], "-map", "0:a:0", "-af", trim, "-c:a", "flac", "-compression_level", "12" };
                        if (info.BitDepth > 16) args.AddRange(new[] { "-sample_fmt", "s32", "-bits_per_raw_sample", info.BitDepth.ToString() });
                        else args.AddRange(new[] { "-sample_fmt", "s16" });
                        args.Add(outPath);
                        r = await Proc.RunAsync(_tools.Ffmpeg, args, token);
                    }
                    if (!r.Ok) lock (errors) errors.Add($"track {seg.Number}: {Short(r.StdErr)}");
                    progress?.Report(Interlocked.Increment(ref done) / (double)segments.Count * 0.7);
                });
                created.AddRange(outputs.Where(o => o != null));
                if (errors.Count > 0)
                {
                    rel.AddFlag(Severity.Error, Stages.Split, "Encoding failed: " + string.Join("; ", errors.Take(3)));
                    Rollback(created);
                    return outcome;
                }

                if (_s.VerifySplitsBitExact)
                {
                    int bits = infos.Values.Max(i => i.BitDepth);
                    var orderedImages = job.Cue.Files.Where(f => job.Images.ContainsKey(f)).Select(f => job.Images[f]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var src = await PcmTools.HashPcmAsync(_tools.Ffmpeg, orderedImages, bits, ct);
                    progress?.Report(0.85);
                    var dst = await PcmTools.HashPcmAsync(_tools.Ffmpeg, outputs, bits, ct);
                    progress?.Report(0.98);
                    if (src.Hash != dst.Hash || src.Bytes != dst.Bytes)
                    {
                        rel.AddFlag(Severity.Error, Stages.Split, $"Verification FAILED: tracks don't add up to the image ({src.Bytes} vs {dst.Bytes} bytes). Image kept, tracks removed.");
                        Rollback(created);
                        return outcome;
                    }
                    outcome.Summary = $"Split {segments.Count} tracks, verified bit-identical (SHA-256 {src.Hash[..12]}…)";
                }
                else outcome.Summary = $"Split {segments.Count} tracks (verification off)";

                // MusicBrainz disc ID for exact pressing lookup.
                if (job.Images.Count == 1)
                {
                    var info = infos.Values.First();
                    var total = info.TotalSamples ?? (dstSamples(outputs, info));
                    var d = DiscId.FromCue(job.Cue, total, info.SampleRate);
                    if (d != null) { outcome.DiscId = d.Value.Id; outcome.Toc = d.Value.Toc; }
                }
            }
            else
            {
                // Lossy: cut on frame boundaries without re-encoding.
                var outputs = new List<string>();
                foreach (var seg in segments)
                {
                    var ext = Path.GetExtension(seg.Image).ToLowerInvariant();
                    var outPath = Path.Combine(job.OutputFolder, TrackFileName(seg, ext));
                    var info = infos[seg.Image];
                    var args = new List<string> { "-v", "error", "-nostdin", "-y", "-ss", Sec(seg.StartSample, info.SampleRate) };
                    if (seg.EndSample.HasValue) args.AddRange(new[] { "-to", Sec(seg.EndSample.Value, info.SampleRate) });
                    args.AddRange(new[] { "-i", seg.Image, "-map", "0:a:0", "-c", "copy", "-map_metadata", "-1", outPath });
                    var r = await Proc.RunAsync(_tools.Ffmpeg, args, ct);
                    outputs.Add(outPath);
                    created.Add(outPath);
                    if (!r.Ok)
                    {
                        rel.AddFlag(Severity.Error, Stages.Split, $"Cutting track {seg.Number} failed: {Short(r.StdErr)}");
                        Rollback(created);
                        return outcome;
                    }
                    progress?.Report(++done / (double)segments.Count * 0.9);
                }
                double srcDur = infos.Values.Sum(i => i.Duration), dstDur = 0;
                foreach (var o in outputs) dstDur += (await AudioProbe.ProbeAsync(_tools.Ffprobe, o, ct))?.Duration ?? 0;
                if (Math.Abs(srcDur - dstDur) > 1.5)
                {
                    rel.AddFlag(Severity.Error, Stages.Split, $"Lossy split lengths don't add up ({srcDur:0.0}s vs {dstDur:0.0}s). Image kept, tracks removed.");
                    Rollback(created);
                    return outcome;
                }
                outcome.Summary = $"Cut {segments.Count} {Path.GetExtension(segments[0].Image).TrimStart('.').ToUpperInvariant()} tracks without re-encoding";
            }

            // Tags from the image + cue.
            var imgTags = TagIO.Read(job.Images.Values.First());
            int audioTrackCount = segments.Count(s => !s.Hidden);
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var path = created[i];
                var ts = imgTags.Clone();
                ts.Title = seg.Hidden ? "Hidden Track" : seg.Track?.Title ?? $"Track {seg.Number:00}";
                ts.Artist = seg.Track?.Performer ?? job.Cue.Performer ?? imgTags.Artist;
                ts.AlbumArtist = job.Cue.Performer ?? imgTags.AlbumArtist ?? imgTags.Artist;
                ts.Album = job.Cue.Title ?? imgTags.Album;
                ts.Year = TextUtil.ParseYear(job.Cue.Date) ?? imgTags.Year;
                ts.Genre = job.Cue.Genre ?? imgTags.Genre;
                ts.Track = seg.Number;
                ts.TrackTotal = audioTrackCount;
                ts.Disc = job.DiscNumber;
                ts.Isrc = seg.Track?.Isrc;
                ts.Label = job.Cue.Label ?? imgTags.Label;
                if (!string.IsNullOrWhiteSpace(job.Cue.Catalog) && job.Cue.Catalog.All(char.IsDigit) && job.Cue.Catalog.Trim('0').Length > 0)
                    ts.Barcode = job.Cue.Catalog;
                try { TagIO.Write(path, ts, _s); }
                catch (Exception e) { rel.AddFlag(Severity.Warning, Stages.Split, $"Couldn't tag {Path.GetFileName(path)}: {e.Message}"); }

                var info = await AudioProbe.ProbeAsync(_tools.Ffprobe, path, ct);
                outcome.Tracks.Add(new TrackFile
                {
                    Path = path,
                    OriginalFileName = Path.GetFileName(path),
                    Disc = job.DiscNumber,
                    Number = seg.Number,
                    Duration = info?.Duration ?? 0,
                    Codec = info?.Codec ?? "",
                    Lossless = info?.Lossless ?? lossless,
                    SampleRate = info?.SampleRate ?? 0,
                    BitDepth = info?.BitDepth ?? 0,
                    BitrateKbps = info?.BitrateKbps ?? 0,
                    Before = ts.Clone(),
                });
            }

            // Originals go only after everything above succeeded.
            if (_s.RemoveImagesAfterVerifiedSplit && (_s.VerifySplitsBitExact || !lossless))
            {
                foreach (var f in job.Images.Values.Concat(job.Redundant).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (DeferRemoval) { outcome.ToRemove.Add(f); continue; }
                    try { _ops.Remove(f); }
                    catch (Exception e) { rel.AddFlag(Severity.Warning, Stages.Split, $"Couldn't remove {Path.GetFileName(f)}: {e.Message}"); }
                }
            }
            outcome.Success = true;
            progress?.Report(1);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            Rollback(created);
            throw;
        }
        catch (Exception e)
        {
            rel.AddFlag(Severity.Error, Stages.Split, $"Split crashed: {e.Message}. Image kept.");
            Rollback(created);
            return outcome;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        static long dstSamples(IEnumerable<string> outs, AudioInfo info) => (long)Math.Round(info.Duration * info.SampleRate);
    }

    private string TrackFileName(Segment seg, string ext)
    {
        var title = seg.Hidden ? "Hidden Track" : string.IsNullOrWhiteSpace(seg.Title) ? $"Track {seg.Number:00}" : seg.Title!;
        return TextUtil.SanitizeName($"{seg.Number:00} - {title}", _s.ColonReplacement, _s.IllegalCharReplacement) + ext;
    }

    private static string Sec(long samples, int rate) => (samples / (double)rate).ToString("0.000000", CultureInfo.InvariantCulture);

    private static void Rollback(IEnumerable<string> created)
    {
        foreach (var c in created) { try { if (File.Exists(c)) File.Delete(c); } catch { } }
    }

    private static string Short(string s)
    {
        s = s.Trim();
        return s.Length > 240 ? s[^240..] : s;
    }
}
