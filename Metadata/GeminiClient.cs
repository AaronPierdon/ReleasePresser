using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pressing.Core.Metadata;

/// <summary>Last-resort identification with Gemini + Google Search grounding. Only called for releases the free sources couldn't settle.</summary>
public sealed class GeminiClient
{
    private readonly string _key;
    private readonly string _model;

    public GeminiClient(string key, string model)
    {
        _key = key;
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-flash-latest" : model.Trim();
    }

    public string? LastError { get; private set; }
    /// <summary>
    /// One Gemini question at a time (it's paced to one per 6 s anyway). Callers that read <see cref="LastError"/>
    /// hold this while asking, so with several albums in parallel each one gets its own error message.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
    /// <summary>Set once the daily quota is gone; later calls in the same run return immediately.</summary>
    public bool QuotaExhausted { get; private set; }

    public async Task<JsonNode?> AskJsonAsync(string prompt, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent";
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt })
            }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.1 }
        };
        if (QuotaExhausted) { LastError = "Gemini skipped: it refused earlier in this run (quota / rate limit), so it isn't asked again until the next run."; return null; }
        LastError = null;
        int refused = 0;
        const int attempts = 3;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // Free-tier Gemini allows only a handful of requests per minute: pace them.
            await Http.WaitTurnAsync("generativelanguage.googleapis.com", TimeSpan.FromSeconds(6), ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", _key);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Pressing.Core.Util.RunLog.Write("ai", $"POST Gemini {_model} (attempt {attempt + 1}, prompt {prompt.Length / 1024.0:0.#} KB, Google Search on)…");
                using var resp = await Http.Client.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                int code = (int)resp.StatusCode;
                Pressing.Core.Util.RunLog.Write("ai", $"Gemini → HTTP {code} in {Pressing.Core.Util.RunLog.Ms(sw.Elapsed)}, {text.Length / 1024.0:0.#} KB");
                Pressing.Core.Util.RunLog.Record("ai", $"Gemini {_model}", sw.Elapsed);
                if (code is 429 or 500 or 503)
                {
                    if (code == 429) refused++;
                    Pressing.Core.Util.RunLog.Write("ai", "Gemini says: " + Trim(Regex.Replace(text, @"\s+", " ")));
                    LastError = code == 429
                        ? "Gemini refused: rate limit or daily quota reached (HTTP 429). Free keys allow only a few requests per minute and a limited number per day."
                        : $"Gemini temporarily unavailable (HTTP {code}).";
                    // Honour the server's suggested wait ("retryDelay": "23s") when it gives one.
                    if (attempt == attempts - 1)
                    {
                        // Refused every time: don't make every other album wait through the same back-off.
                        if (refused == attempts) { QuotaExhausted = true; LastError += " Gemini won't be asked again this run."; }
                        Pressing.Core.Util.RunLog.Write("ai", "Gemini gave up after " + attempts + " attempts" + (QuotaExhausted ? " — AI lookups are switched off for the rest of this run." : "."));
                        return null;
                    }
                    var wait = TimeSpan.FromSeconds(10 * (attempt + 1));
                    var m = Regex.Match(text, "\"retryDelay\"\\s*:\\s*\"(\\d+)s\"");
                    if (m.Success) wait = TimeSpan.FromSeconds(Math.Min(90, int.Parse(m.Groups[1].Value) + 1));
                    if (resp.Headers.RetryAfter?.Delta is { } ra) wait = ra < TimeSpan.FromSeconds(90) ? ra : TimeSpan.FromSeconds(90);
                    if (text.Contains("PerDay", StringComparison.OrdinalIgnoreCase)) { QuotaExhausted = true; LastError = "Gemini daily quota used up (HTTP 429). AI lookups resume tomorrow, or use a paid key."; return null; }
                    Pressing.Core.Util.RunLog.RecordWait("retry back-off: Gemini", wait);
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    LastError = code == 404
                        ? $"Gemini model \"{_model}\" not found (HTTP 404). Check the model ID in Settings."
                        : code is 400 or 401 or 403
                            ? $"Gemini rejected the request (HTTP {code}) — check the API key in Settings. {Trim(text)}"
                            : $"Gemini HTTP {code}: {Trim(text)}";
                    return null;
                }
                var root = JsonNode.Parse(text);
                var parts = root?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                var answer = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>() ?? "") ?? Array.Empty<string>());
                var json = ExtractJson(answer);
                if (json == null) { LastError = "Gemini answer had no JSON: " + Trim(answer); return null; }
                return JsonNode.Parse(json);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                LastError = e.Message;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        return null;
    }

    private static string? ExtractJson(string s)
    {
        var fence = Regex.Match(s, "```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```");
        if (fence.Success) return fence.Groups[1].Value;
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : null;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ------------------------------------------------------------------ identification

    public const string IdentifyInstructions = """
You are a meticulous music librarian. A folder of audio files (loose files and folders from a download or rip)
needs the correct release and per-track metadata. Use Google Search. Look at MusicBrainz, Discogs, Bandcamp,
the label's site, Apple Music/YouTube Music listings, and liner-note sources. Specifically consider special,
deluxe, expanded, bonus, live, Japanese, promo, rare, and mainstream editions so that EVERY local file gets data;
bonus tracks usually come from a specific edition.

Rules:
- Match using track count, track durations (seconds), catalog numbers, barcodes, cue/log text, folder and file names.
- album: the official release title with official capitalization. No edition notes unless they are part of the title.
- album_artist: ONE primary artist name only (as shown on Discogs/YouTube Music), never "X feat. Y".
  For a various-artists compilation use "Various Artists".
- track artists: a list of individual artist names (split featured artists out).
- title: the official track title only — no track numbers, no artist prefix, no file extension.
- year: the year of the edition these files are; original_year: first release of the album.
- If you are not sure, lower "confidence". Never invent tracks. If nothing fits at all, set "matched": false.

Reply with ONLY this JSON (no prose):
{"matched":true,"confidence":0.0,"album":"","album_artist":"","year":0,"original_year":0,"label":"","catalog_number":"",
 "edition":"","is_compilation":false,"source_urls":[""],
 "tracks":[{"file":"exact local file name","disc":1,"track":1,"title":"","artists":[""]}],
 "notes":"short reasoning"}
""";

    public async Task<Candidate?> IdentifyAsync(JsonObject context, CancellationToken ct)
    {
        var prompt = IdentifyInstructions + "\n\nLOCAL CONTEXT (JSON):\n" + context.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var j = await AskJsonAsync(prompt, ct);
        if (j == null) return null;
        if (string.Equals(j["matched"]?.ToString(), "false", StringComparison.OrdinalIgnoreCase)) { LastError = "AI: no match. " + j["notes"]; return null; }

        var c = new Candidate
        {
            Source = "AI",
            Album = Str(j["album"]),
            AlbumArtists = new List<string> { Str(j["album_artist"]) }.Where(x => x.Length > 0).ToList(),
            Year = Int(j["year"]),
            OriginalYear = Int(j["original_year"]),
            Label = NullIfEmpty(Str(j["label"])),
            CatalogNumber = NullIfEmpty(Str(j["catalog_number"])),
            Edition = NullIfEmpty(Str(j["edition"])),
            IsCompilation = j["is_compilation"]?.GetValue<bool>() ?? false,
            Url = j["source_urls"]?.AsArray().Select(u => u?.ToString()).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            Why = Str(j["notes"]),
        };
        double conf = 0;
        try { conf = j["confidence"]?.GetValue<double>() ?? 0; } catch { }
        c.Score = Math.Clamp(conf, 0, 1);

        foreach (var t in j["tracks"]?.AsArray() ?? new JsonArray())
        {
            if (t == null) continue;
            var disc = Int(t["disc"]) ?? 1;
            var med = c.Media.FirstOrDefault(m => m.Position == disc);
            if (med == null) { med = new CandidateMedium { Position = disc }; c.Media.Add(med); }
            med.Tracks.Add(new CandidateTrack
            {
                Disc = disc,
                Position = Int(t["track"]) ?? med.Tracks.Count + 1,
                Title = Str(t["title"]),
                Artists = t["artists"]?.AsArray().Select(a => a?.ToString() ?? "").Where(a => a.Length > 0).ToList() ?? new(),
                FileName = NullIfEmpty(Str(t["file"])),
            });
        }
        c.Media = c.Media.OrderBy(m => m.Position).ToList();
        return c;
    }

    // ------------------------------------------------------------------ dupe tiebreak

    public async Task<(int Index, double Confidence, string Reason)?> PickBestMasteringAsync(string artist, string album, IReadOnlyList<string> versions, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"I have several versions of \"{album}\" by {artist}. Which one has the best, most audiophile-praised sound quality");
        sb.AppendLine("(mastering most praised by listeners and reviewers: e.g. Steve Hoffman forums, DR database, audiophile reviews)?");
        sb.AppendLine("Measured DR (dynamic range) values are included; higher usually means less brickwall limiting. Use Google Search.");
        sb.AppendLine("Versions:");
        for (int i = 0; i < versions.Count; i++) sb.AppendLine($"[{i}] {versions[i]}");
        sb.AppendLine("Reply with ONLY JSON: {\"best_index\":0,\"confidence\":0.0,\"reason\":\"one sentence\"}. If there is no clear consensus, set confidence below 0.5.");
        var j = await AskJsonAsync(sb.ToString(), ct);
        if (j == null) return null;
        int? idx = int.TryParse(j["best_index"]?.ToString(), out var bi) ? bi : null; // 0 is a valid answer here
        if (idx == null || idx < 0 || idx >= versions.Count) return null;
        double conf = 0;
        try { conf = j["confidence"]?.GetValue<double>() ?? 0; } catch { }
        return (idx.Value, conf, Str(j["reason"]));
    }

    private static string Str(JsonNode? n) => n?.ToString().Trim() ?? "";
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static int? Int(JsonNode? n)
    {
        if (n == null) return null;
        return int.TryParse(n.ToString(), out var v) && v > 0 ? v : null;
    }
}
