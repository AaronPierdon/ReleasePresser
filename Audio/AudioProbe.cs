using System.Globalization;
using System.Text.Json;
using Pressing.Core.Util;

namespace Pressing.Core.Audio;

public sealed class AudioInfo
{
    public string Codec { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    /// <summary>Real bit depth of the samples (24 for 24-bit FLAC even though ffmpeg decodes to s32).</summary>
    public int BitDepth { get; set; }
    public string SampleFormat { get; set; } = "";
    public double Duration { get; set; }
    public long? TotalSamples { get; set; }
    public int BitrateKbps { get; set; }
    public bool Lossless => Formats.IsLosslessCodec(Codec);
    /// <summary>Floating-point lossless PCM (lossy decoders always output float, so they never count).</summary>
    public bool IsFloat => Lossless && (SampleFormat.StartsWith("flt") || SampleFormat.StartsWith("dbl") || Codec.StartsWith("pcm_f"));
}

public static class Formats
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".flac", ".ape", ".wav", ".wv", ".tta", ".tak", ".aiff", ".aif", ".m4a", ".alac", ".mp3", ".ogg", ".opus", ".aac", ".wma", ".dsf", ".dff", ".mpc" };

    public static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".flac", ".ape", ".wav", ".wv", ".tta", ".tak", ".aiff", ".aif", ".alac" };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static bool IsLosslessCodec(string codec) => codec switch
    {
        "flac" or "ape" or "wavpack" or "tta" or "tak" or "alac" or "mlp" or "truehd" => true,
        _ => codec.StartsWith("pcm_") || codec.StartsWith("dsd_"),
    };

    /// <summary>Preference when the same image exists in several formats (lower = better).</summary>
    public static int ImagePreference(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => 0, ".wav" => 1, ".wv" => 2, ".aiff" or ".aif" => 3, ".ape" => 4, ".tta" => 5, ".tak" => 6,
        ".m4a" => 7, ".mp3" => 20, ".ogg" or ".opus" => 21, _ => 30
    };
}

public static class AudioProbe
{
    public static async Task<AudioInfo?> ProbeAsync(string ffprobe, string file, CancellationToken ct)
    {
        var r = await Proc.RunAsync(ffprobe, new[]
        {
            "-v", "error", "-select_streams", "a:0", "-print_format", "json",
            "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_raw_sample,bits_per_sample,sample_fmt,duration,duration_ts,time_base,bit_rate:format=duration,bit_rate",
            file
        }, ct);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
            var s = streams[0];
            var info = new AudioInfo
            {
                Codec = Str(s, "codec_name"),
                SampleRate = Int(s, "sample_rate"),
                Channels = Int(s, "channels"),
                SampleFormat = Str(s, "sample_fmt"),
            };
            int raw = Int(s, "bits_per_raw_sample"), bps = Int(s, "bits_per_sample");
            info.BitDepth = raw > 0 ? raw : bps > 0 ? bps : info.SampleFormat switch
            {
                "s16" or "s16p" => 16, "s32" or "s32p" => 32, "u8" or "u8p" => 8, _ => 0
            };
            if (!info.Lossless) info.BitDepth = 0;

            double dur = Dbl(s, "duration");
            if (dur <= 0 && root.TryGetProperty("format", out var fmt)) dur = Dbl(fmt, "duration");
            info.Duration = dur;

            // FLAC and WAV report duration_ts in samples when time_base is 1/sample_rate.
            var tb = Str(s, "time_base");
            long dts = (long)Dbl(s, "duration_ts");
            if (dts > 0 && tb == $"1/{info.SampleRate}") info.TotalSamples = dts;

            var br = Int(s, "bit_rate");
            if (br <= 0 && root.TryGetProperty("format", out var f2)) br = Int(f2, "bit_rate");
            info.BitrateKbps = br / 1000;
            return info;
        }
        catch { return null; }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ToString() : "";
    private static int Int(JsonElement e, string n) => int.TryParse(Str(e, n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Dbl(JsonElement e, string n) => double.TryParse(Str(e, n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
