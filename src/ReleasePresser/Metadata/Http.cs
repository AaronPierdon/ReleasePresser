using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

/// <summary>Shared HTTP client with per-host throttling (MusicBrainz allows 1 request/second).</summary>
public static class Http
{
    public static readonly HttpClient Client = CreateClient();
    private static readonly Dictionary<string, (SemaphoreSlim Gate, DateTime Last)> Hosts = new();
    private static readonly object HostsLock = new();

    public static string UserAgent { get; set; } = "Pressing/0.1 ( personal music library tool )";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        return c;
    }

    /// <summary>A URL safe to show in the log: API keys and tokens replaced by ***.</summary>
    public static string Redact(string url) =>
        Regex.Replace(url, @"(?i)([?&](?:key|client|token|api_key|secret)=)[^&]+", "$1***");

    /// <summary>Short label for summaries: "musicbrainz.org /ws/2/release".</summary>
    public static string Label(string url)
    {
        try
        {
            var u = new Uri(url);
            var path = Regex.Replace(u.AbsolutePath, @"/[0-9a-f\-]{20,}|/\d{3,}", "/{id}");
            return u.Host + " " + path;
        }
        catch { return url; }
    }

    public static async Task<JsonDocument?> GetJsonAsync(string url, TimeSpan minInterval, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        var shown = Redact(url);
        var total = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await WaitTurnAsync(new Uri(url).Host, minInterval, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            configure?.Invoke(req);
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await Client.SendAsync(req, ct);
                int code = (int)resp.StatusCode;
                if (code == 429 || code == 503)
                {
                    var back = TimeSpan.FromSeconds(2 + attempt * 3);
                    RunLog.Write("http", $"GET {shown} → HTTP {code} after {RunLog.Ms(sw.Elapsed)} (busy / rate-limited) — retry {attempt + 1} in {RunLog.Ms(back)}");
                    RunLog.RecordWait($"retry back-off: {new Uri(url).Host}", back);
                    await Task.Delay(back, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    RunLog.Write("http", $"GET {shown} → HTTP {code} in {RunLog.Ms(sw.Elapsed)}{(code == 404 ? " (not found)" : "")}");
                    RunLog.Record("http", Label(url), sw.Elapsed);
                    return null;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                RunLog.Write("http", $"GET {shown} → {code} in {RunLog.Ms(sw.Elapsed)}, {bytes.Length / 1024.0:0.#} KB{(attempt > 0 ? $" (attempt {attempt + 1}, {RunLog.Ms(total.Elapsed)} incl. retries)" : "")}");
                RunLog.Record("http", Label(url), sw.Elapsed);
                return JsonDocument.Parse(bytes);
            }
            catch (HttpRequestException e) when (attempt < 3)
            {
                RunLog.Write("http", $"GET {shown} → network error after {RunLog.Ms(sw.Elapsed)}: {e.Message} — retry {attempt + 1} in 2 s");
                RunLog.RecordWait($"retry back-off: {new Uri(url).Host}", TimeSpan.FromSeconds(2));
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
            {
                RunLog.Write("http", $"GET {shown} → TIMED OUT after {RunLog.Ms(sw.Elapsed)} — retry {attempt + 1}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                RunLog.Write("http", $"GET {shown} → failed after {RunLog.Ms(sw.Elapsed)}: {e.Message}");
                RunLog.Record("http", Label(url), sw.Elapsed);
                throw;
            }
        }
        RunLog.Write("http", $"GET {shown} → gave up after 4 attempts, {RunLog.Ms(total.Elapsed)}");
        RunLog.Record("http", Label(url), total.Elapsed);
        return null;
    }

    public static async Task WaitTurnAsync(string host, TimeSpan minInterval, CancellationToken ct)
    {
        (SemaphoreSlim Gate, DateTime Last) entry;
        lock (HostsLock)
        {
            if (!Hosts.TryGetValue(host, out entry)) Hosts[host] = entry = (new SemaphoreSlim(1, 1), DateTime.MinValue);
        }
        await entry.Gate.WaitAsync(ct);
        try
        {
            DateTime last;
            lock (HostsLock) last = Hosts[host].Last;
            var wait = last + minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                if (wait.TotalMilliseconds >= 1500) RunLog.Write("wait", $"{host}: waiting {RunLog.Ms(wait)} (rate limit: one request per {RunLog.Ms(minInterval)})");
                RunLog.RecordWait($"rate limit: {host}", wait);
                await Task.Delay(wait, ct);
            }
            lock (HostsLock) Hosts[host] = (entry.Gate, DateTime.UtcNow);
        }
        finally { entry.Gate.Release(); }
    }

    // JSON helpers
    public static string? S(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    public static IEnumerable<JsonElement> A(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static JsonElement O(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
