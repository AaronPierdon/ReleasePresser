using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pressing.Core.Models;
using Pressing.Core.Tags;
using Pressing.Core.Util;

namespace Pressing.Core.Metadata;

/// <summary>
/// Works out the correct release for a folder of tracks and writes clean tags.
/// Order: existing MusicBrainz ID → disc ID from cue → MusicBrainz search → Discogs → AcoustID → Gemini.
/// Stops as soon as something clears the accept threshold. Unmatched bonus tracks keep their (cleaned) tags.
/// </summary>
public sealed class MetadataResolver
{
    private readonly PressingSettings _s;
    private readonly Toolset _tools;
    private readonly Action<string> _log;
    private readonly MusicBrainzClient _mb = new();
    private readonly DiscogsClient? _discogs;
    private readonly AcoustIdClient? _acoustid;
    private readonly GeminiClient? _ai;

    public MetadataResolver(PressingSettings s, Toolset tools, Action<string> log)
    {
        _s = s; _tools = tools; _log = log;
        if (!string.IsNullOrWhiteSpace(s.MusicBrainzContact)) Http.UserAgent = $"Pressing/0.1 ( {s.MusicBrainzContact} )";
        if (s.UseDiscogs && !string.IsNullOrWhiteSpace(s.DiscogsToken)) _discogs = new DiscogsClient(s.DiscogsToken);
        if (s.UseAcoustId && !string.IsNullOrWhiteSpace(s.AcoustIdKey) && tools.Fpcalc != null) _acoustid = new AcoustIdClient(s.AcoustIdKey, tools.Fpcalc);
        if (s.UseAi && !string.IsNullOrWhiteSpace(s.GeminiApiKey)) _ai = new GeminiClient(s.GeminiApiKey, s.GeminiModel);
    }

    public GeminiClient? Ai => _ai;

    public async Task ResolveAsync(Release rel, LibraryMode mode, string rootName, CancellationToken ct)
    {
        var local = Matcher.LocalOrder(rel);
        if (local.Count == 0) { rel.AddFlag(Severity.Warning, Stages.Metadata, "No audio tracks to tag."); return; }

        var (albumHint, artistHint, yearHint) = Hints(rel, mode, rootName);
        _log($"  hints: \"{albumHint}\" by \"{artistHint}\" ({yearHint?.ToString() ?? "no year"}), {local.Count} tracks");

        var results = new List<MatchResult>();
        MatchResult? Best() => results.OrderByDescending(r => r.Score).FirstOrDefault();
        bool Good() => Best() is { } b && b.Score >= _s.AcceptConfidence && b.Unmatched == 0;
        async Task Try(Func<Task<IEnumerable<Candidate?>>> fetch, string label)
        {
            if (Good()) { RunLog.Write("lookup", $"skip {label}: already have a confident match ({Best()!.Candidate.Source} {Best()!.Score:0.00})"); return; }
            using var timer = RunLog.Time("lookup", $"{label}: {rel.OriginalName}");
            try
            {
                var found = (await fetch()).Where(c => c != null).ToList();
                timer.Result = $"{found.Count} candidate(s)";
                foreach (var c in found)
                {
                    if (results.Any(r => r.Candidate.Source == c!.Source && r.Candidate.Id == c.Id)) continue;
                    var m = Matcher.Score(rel, c!, albumHint, artistHint);
                    results.Add(m);
                    _log($"  {label}: {c!.Source} \"{c.Album}\" {c.Year} [{c.TrackCount} tr] → {m.Score:0.00} ({m.Matched}/{local.Count})");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { timer.Result = "FAILED: " + e.Message; rel.AddFlag(Severity.Info, Stages.Metadata, $"{label} lookup failed: {e.Message}"); }
        }

        if (_s.UseMusicBrainz)
        {
            var mbid = local.Select(t => t.Before.MusicBrainzReleaseId).Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (mbid != null) await Try(async () => new[] { await _mb.GetReleaseAsync(mbid, ct) }, "tagged MBID");
            if (rel.MusicBrainzDiscId != null) await Try(async () => await _mb.ByDiscIdAsync(rel.MusicBrainzDiscId, rel.CdToc, ct), "disc ID");
            if (!string.IsNullOrWhiteSpace(albumHint))
            {
                await Try(async () =>
                {
                    var hits = await _mb.SearchAsync(albumHint, artistHint, local.Count, ct);
                    if (hits.Count == 0 && artistHint != null) hits = await _mb.SearchAsync(albumHint, null, local.Count, ct);
                    var list = new List<Candidate?>();
                    foreach (var h in hits.Take(4)) list.Add(await _mb.GetReleaseAsync(h.Id, ct));
                    return list;
                }, "MusicBrainz");
            }
        }

        if (_discogs != null)
        {
            var catno = local.Select(t => t.Before.CatalogNumber).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            await Try(async () =>
            {
                var ids = new List<string>();
                if (catno != null) ids.AddRange(await _discogs.SearchByCatalogAsync(catno, ct));
                if (!string.IsNullOrWhiteSpace(albumHint)) ids.AddRange(await _discogs.SearchAsync(albumHint, mode == LibraryMode.Collection ? null : artistHint, ct));
                var list = new List<Candidate?>();
                foreach (var id in ids.Distinct().Take(4)) list.Add(await _discogs.GetReleaseAsync(id, _s.YearMode == YearMode.Original, ct));
                return list;
            }, "Discogs");
        }

        if (_acoustid != null)
        {
            await Try(async () =>
            {
                var ids = await _acoustid.VoteReleasesAsync(local.Select(t => t.Path), local.Count, ct);
                var list = new List<Candidate?>();
                foreach (var id in ids.Take(3)) list.Add(await _mb.GetReleaseAsync(id, ct));
                return list;
            }, "AcoustID");
        }

        var best = Best();
        // AI: when nothing is good enough, or when the best match leaves bonus tracks without data.
        if (_ai == null) RunLog.Write("lookup", "AI: off (no Gemini key or turned off in Settings)");
        else if (best != null && best.Score >= _s.AcceptConfidence && best.Unmatched == 0) RunLog.Write("lookup", "skip AI: database match is confident and covers every track");
        if (_ai != null && (best == null || best.Score < _s.AcceptConfidence || best.Unmatched > 0))
        {
            var why = best == null ? "no database match" : best.Score < _s.AcceptConfidence ? $"best match only {best.Score:0.00}" : $"{best.Unmatched} track(s) not on {best.Candidate.Source}'s tracklist";
            Candidate? aiCand;
            using (var timer = RunLog.Time("lookup", $"AI (Gemini + Google Search): {rel.OriginalName} — because {why}"))
            {
                var ctx = BuildContext(rel, mode, rootName, albumHint, artistHint, best);
                string? aiError;
                await _ai.Gate.WaitAsync(ct);
                try { aiCand = await _ai.IdentifyAsync(ctx, ct); aiError = _ai.LastError; }
                finally { _ai.Gate.Release(); }
                timer.Result = aiCand == null ? "no answer: " + (aiError ?? "no reason given") : $"\"{aiCand.Album}\" conf {aiCand.Score:0.00}";
                if (aiCand == null) rel.AddFlag(Severity.Info, Stages.Metadata, "AI lookup gave no answer: " + (aiError ?? "no reason given"));
            }
            if (aiCand == null)
            {
                // (reason already noted above)
            }
            else
            {
                var m = Matcher.Score(rel, aiCand, albumHint, artistHint);
                _log($"  AI: \"{aiCand.Album}\" {aiCand.Year} conf {aiCand.Score:0.00} → {m.Score:0.00} ({m.Matched}/{local.Count})");
                if (best != null && best.Score >= _s.MinimumConfidence && best.Unmatched > 0 && m.Score < best.Score)
                {
                    // Keep the database match; borrow AI data only for the tracks it couldn't place.
                    foreach (var (t, ct2) in m.Map.Where(kv => kv.Value != null && best.Map.GetValueOrDefault(kv.Key) == null))
                        best.Map[t] = ct2;
                    best.Candidate.Why += " Bonus tracks filled by AI.";
                }
                else results.Add(m);
            }
        }

        best = Best();
        if (best == null || best.Score < _s.MinimumConfidence)
        {
            ApplyFallback(rel, mode, albumHint, artistHint, yearHint);
            rel.AddFlag(Severity.Warning, Stages.Metadata,
                best == null ? "No online match on any source; kept cleaned existing tags." :
                $"Best match too weak ({best.Candidate.Source} \"{best.Candidate.Album}\", {best.Score:0.00}); kept cleaned existing tags.");
        }
        else
        {
            Apply(rel, mode, best, artistHint);
            if (best.Score < _s.AcceptConfidence)
                rel.AddFlag(Severity.Warning, Stages.Metadata, $"Low-confidence match ({best.Score:0.00}) — please check.");
            if (best.Unmatched > 0)
                rel.AddFlag(Severity.Info, Stages.Metadata, $"{best.Unmatched} track(s) not on {best.Candidate.Source}'s tracklist; treated as bonus tracks with cleaned tags.");
        }

    }

    // ------------------------------------------------------------------ hints

    /// <summary>Tags good enough to trust as-is: every track has title, artist, album and number, and one album name.</summary>
    public static bool HasCompleteTags(Release rel)
    {
        var t = rel.AllTracks.ToList();
        if (t.Count == 0) return false;
        bool each = t.All(x => !string.IsNullOrWhiteSpace(x.Before.Title) && !string.IsNullOrWhiteSpace(x.Before.Artist)
                               && !string.IsNullOrWhiteSpace(x.Before.Album) && x.Before.Track is > 0);
        bool oneAlbum = t.Select(x => StripDiscSuffix(x.Before.Album ?? "").ToLowerInvariant()).Distinct().Count() == 1;
        bool year = t.Any(x => x.Before.Year is > 0);
        return each && oneAlbum && year;
    }

    /// <summary>For already-tidy releases: no online lookups, just normalise the existing tags.</summary>
    public void KeepExisting(Release rel, LibraryMode mode, string rootName)
    {
        var (albumHint, artistHint, yearHint) = Hints(rel, mode, rootName);
        ApplyFallback(rel, mode, albumHint, artistHint, yearHint, keepAlbum: true);
        rel.Match = new MetadataMatch { Source = "Existing tags", Confidence = 1, Notes = "Tags already complete; no online lookup.", Edition = rel.Match?.Edition, MatchedTracks = rel.AllTracks.Count() };
    }

    private static (string Album, string? Artist, int? Year) Hints(Release rel, LibraryMode mode, string rootName)
    {
        var tracks = rel.AllTracks.ToList();
        string? Consensus(Func<TrackFile, string?> f) => tracks.Select(f).Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!.Trim(), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var artist = Consensus(t => t.Before.AlbumArtist) ?? Consensus(t => TextUtil.SplitArtists(t.Before.Artist).FirstOrDefault());
        if (mode == LibraryMode.Artist && rootName.Length > 0 &&
            (artist == null || artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)))
            artist = rootName;
        else if (artist != null && TextUtil.Normalize(artist) == TextUtil.Normalize(rootName))
            artist = rootName; // "TEST ARTIST" in tags → folder spelling

        var album = Consensus(t => t.Before.Album);
        if (album != null) album = StripDiscSuffix(album);
        var parsed = TextUtil.ParseFolderName(rel.OriginalName, artist);
        if (string.IsNullOrWhiteSpace(album) || album.Length < 2) album = parsed.Album;
        var year = (int?)tracks.Select(t => t.Before.Year).Where(y => y > 0).GroupBy(y => y).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? parsed.Year;
        if (year == 0) year = parsed.Year;
        return (album, artist, year);
    }

    private static string StripDiscSuffix(string album) =>
        Regex.Replace(album, @"([\s\-_]+|[\(\[]?)(CD|DISC|DISK)\s*\d+[\)\]]?\s*$", "", RegexOptions.IgnoreCase).Trim().TrimEnd('-').Trim();

    // ------------------------------------------------------------------ AI context

    private static JsonObject BuildContext(Release rel, LibraryMode mode, string rootName, string album, string? artist, MatchResult? best)
    {
        var tracks = new JsonArray();
        foreach (var t in Matcher.LocalOrder(rel))
        {
            tracks.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(t.Path),
                ["folder"] = Path.GetFileName(Path.GetDirectoryName(t.Path)),
                ["disc"] = t.Disc,
                ["track"] = t.Number,
                ["duration_sec"] = Math.Round(t.Duration, 1),
                ["format"] = $"{t.Codec} {(t.Lossless ? $"{t.BitDepth}/{t.SampleRate}" : $"{t.BitrateKbps}kbps")}",
                ["tags"] = new JsonObject
                {
                    ["title"] = t.Before.Title, ["artist"] = t.Before.Artist, ["album"] = t.Before.Album,
                    ["albumartist"] = t.Before.AlbumArtist, ["year"] = t.Before.Year, ["label"] = t.Before.Label,
                    ["catalog"] = t.Before.CatalogNumber, ["isrc"] = t.Before.Isrc,
                },
            });
        }
        var docs = string.Join("\n", rel.ContextDocs);
        if (docs.Length > 6000) docs = docs[..6000] + "\n…(truncated)";
        var ctx = new JsonObject
        {
            ["library_folder"] = rootName,
            ["library_kind"] = mode == LibraryMode.Collection ? "label or collection folder" : "artist folder",
            ["release_folder"] = rel.OriginalName,
            ["disc_folders"] = new JsonArray(rel.Discs.Select(d => (JsonNode)JsonValue.Create(d.OriginalName)!).ToArray()),
            ["guessed_album"] = album,
            ["guessed_artist"] = artist,
            ["tracks"] = tracks,
            ["cue_log_text"] = docs,
        };
        if (best != null)
        {
            ctx["best_database_guess"] = new JsonObject
            {
                ["source"] = best.Candidate.Source, ["url"] = best.Candidate.Url, ["album"] = best.Candidate.Album,
                ["year"] = best.Candidate.Year, ["track_count"] = best.Candidate.TrackCount, ["score"] = Math.Round(best.Score, 2),
                ["files_not_on_it"] = new JsonArray(best.Map.Where(kv => kv.Value == null).Select(kv => (JsonNode)JsonValue.Create(Path.GetFileName(kv.Key.Path))!).ToArray()),
            };
        }
        return ctx;
    }

    // ------------------------------------------------------------------ applying

    private void Apply(Release rel, LibraryMode mode, MatchResult m, string? artistHint)
    {
        var c = m.Candidate;
        bool compilation = c.IsCompilation || (mode == LibraryMode.Collection && DistinctTrackArtists(m) > 2);
        var primary = c.AlbumArtists.FirstOrDefault(a => !a.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) ?? artistHint ?? "";

        rel.Album = c.Album;
        rel.AlbumArtist = compilation ? "Various Artists" : primary;
        rel.Year = _s.YearMode == YearMode.Original ? c.OriginalYear ?? c.Year : c.Year ?? c.OriginalYear;
        rel.Label = c.Label;
        rel.CatalogNumber = c.CatalogNumber;
        rel.IsCompilation = compilation;
        rel.Match = new MetadataMatch
        {
            Source = c.Source, Url = c.Url, Confidence = Math.Round(m.Score, 2), Edition = c.Edition,
            Notes = c.Why, MatchedTracks = m.Matched, BonusTracks = m.Unmatched,
        };

        foreach (var t in Matcher.LocalOrder(rel))
        {
            var a = t.Before.Clone();
            var ct = m.Map.GetValueOrDefault(t);
            a.Album = c.Album;
            a.Year = rel.Year;
            a.Label = c.Label ?? a.Label;
            a.CatalogNumber = c.CatalogNumber ?? a.CatalogNumber;
            a.Barcode = c.Barcode ?? a.Barcode;
            a.MusicBrainzReleaseId = c.Source == "MusicBrainz" ? c.Id : a.MusicBrainzReleaseId;
            if (ct != null)
            {
                a.Title = KeepQualifier(ct.Title, t.Before.Title);
                a.Artist = JoinArtists(ct.Artists.Count > 0 ? ct.Artists : new List<string> { primary });
                a.Track = ct.Position;
                a.Disc = ct.Disc;
                t.IsBonus = false;
            }
            else
            {
                var existing = TextUtil.SplitArtists(t.Before.Artist);
                a.Title = TextUtil.SmartCase(TextUtil.CleanTitle(t.Before.Title ?? Path.GetFileNameWithoutExtension(t.Path), existing.Append(primary), t.Number));
                a.Artist = JoinArtists(existing.Count > 0 ? existing : new List<string> { primary });
                a.Disc = t.Disc;
                a.Track = null; // numbered below
                t.IsBonus = true;
            }
            a.AlbumArtist = compilation ? a.Artist : primary;
            t.After = a;
        }
        Renumber(rel);
        Tidy(rel);
    }

    private void ApplyFallback(Release rel, LibraryMode mode, string album, string? artistHint, int? year, bool keepAlbum = false)
    {
        var tracks = Matcher.LocalOrder(rel);
        var allArtists = tracks.SelectMany(t => TextUtil.SplitArtists(t.Before.Artist)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool compilation = mode == LibraryMode.Collection && allArtists.Count > 2;
        var primary = artistHint ?? allArtists.FirstOrDefault() ?? "";

        var edition = Regex.Match(album, @"\s*[\(\[]([^\)\]]*(?:remaster|deluxe|expanded|edition|anniversary|reissue|bonus)[^\)\]]*)[\)\]]\s*$", RegexOptions.IgnoreCase);
        if (edition.Success && !keepAlbum) album = album[..edition.Index].Trim();
        rel.Album = keepAlbum ? album : TextUtil.SmartCase(album);
        rel.AlbumArtist = compilation ? "Various Artists" : primary;
        rel.Year = year;
        rel.IsCompilation = compilation;
        rel.Match = new MetadataMatch { Source = "Existing tags", Confidence = 0, MatchedTracks = 0, BonusTracks = 0, Notes = "No online source matched.", Edition = edition.Success ? edition.Groups[1].Value.Trim() : null };
        rel.Label = tracks.Select(t => t.Before.Label).FirstOrDefault(x => x != null);

        foreach (var t in tracks)
        {
            var a = t.Before.Clone();
            var artists = TextUtil.SplitArtists(t.Before.Artist);
            a.Title = TextUtil.SmartCase(TextUtil.CleanTitle(t.Before.Title ?? Path.GetFileNameWithoutExtension(t.Path), artists.Append(primary), t.Number));
            a.Artist = JoinArtists(artists.Count > 0 ? artists : new List<string> { primary });
            a.AlbumArtist = compilation ? a.Artist : primary;
            a.Album = rel.Album;
            a.Year = keepAlbum ? t.Before.Year ?? year : year;
            a.Disc = t.Disc;
            a.Track = t.Before.Track ?? TextUtil.ParseLeadingInt(Path.GetFileName(t.Path));
            t.After = a;
        }
        Renumber(rel);
        Tidy(rel);
    }

    /// <summary>Works out the exact ALBUM tag per track (multi-disc sets can get " (CD1)" appended), so the plan knows what changes.</summary>
    private void SetAlbumTags(Release rel)
    {
        bool multi = rel.Discs.Count > 1 || rel.AllTracks.Select(t => t.After.Disc ?? 1).Distinct().Count() > 1;
        foreach (var t in rel.AllTracks)
            t.AlbumTag = multi && _s.AppendDiscToAlbumTag && t.After.Album != null
                ? $"{t.After.Album} ({_s.DiscFolderPrefix}{t.After.Disc ?? t.Disc})"
                : null;
    }

    private void Tidy(Release rel)
    {
        SetAlbumTags(rel);
        if (!_s.StraightQuotes) return;
        static string? Q(string? x) => x?.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"');
        rel.Album = Q(rel.Album);
        rel.AlbumArtist = Q(rel.AlbumArtist);
        foreach (var t in rel.AllTracks)
        {
            t.After.Title = Q(t.After.Title);
            t.After.Artist = Q(t.After.Artist);
            t.After.AlbumArtist = Q(t.After.AlbumArtist);
            t.After.Album = Q(t.After.Album);
            t.AlbumTag = Q(t.AlbumTag);
        }
    }

    /// <summary>Fills missing/duplicate track numbers and sets track/disc totals per disc.</summary>
    private static void Renumber(Release rel)
    {
        var byDisc = rel.AllTracks.GroupBy(t => t.After.Disc ?? t.Disc).OrderBy(g => g.Key).ToList();
        foreach (var g in byDisc)
        {
            var used = new HashSet<int>();
            var ordered = g.OrderBy(t => t.After.Track ?? int.MaxValue).ThenBy(t => t.Number).ThenBy(t => t.OriginalFileName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var t in ordered.Where(t => t.After.Track.HasValue))
                if (!used.Add(t.After.Track!.Value)) t.After.Track = null;
            int next = used.Count == 0 ? 1 : used.Max() + 1;
            foreach (var t in ordered.Where(t => !t.After.Track.HasValue)) t.After.Track = next++;
            foreach (var t in g) t.After.TrackTotal = g.Count();
        }
        int discTotal = byDisc.Count;
        foreach (var t in rel.AllTracks)
        {
            t.After.DiscTotal = discTotal > 1 ? discTotal : null;
            if (discTotal <= 1) t.After.Disc = t.After.Disc is null or 1 ? (int?)null : t.After.Disc;
            if (t.After.Disc is null && discTotal > 1) t.After.Disc = t.Disc;
        }
        // Keep the in-memory disc/number in sync for renaming.
        foreach (var t in rel.AllTracks)
        {
            t.Number = t.After.Track ?? t.Number;
        }
    }

    private static readonly Regex Qualifier = new(@"[\(\[]([^\)\]]*\b(version|mix|remix|edit|rework|re-rub|live|acoustic|demo|instrumental|dub|extended|radio|reprise|session|vip|unplugged|orchestral|piano|ambient|club)\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Databases sometimes list a track without the version your file is (e.g. "38 Weeks" for "38 Weeks (KOKO Version)").
    /// Keep the file's version/mix qualifier when the official title lacks it, so different versions never end up with the same title.
    /// </summary>
    public static string KeepQualifier(string official, string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return official;
        if (Qualifier.IsMatch(official)) return official; // the database names its own version; trust it
        var have = TextUtil.Normalize(official);
        foreach (Match q in Qualifier.Matches(existing))
        {
            var words = TextUtil.Normalize(q.Groups[1].Value);
            if (words.Length > 0 && !have.Contains(words)) official = $"{official} ({q.Groups[1].Value.Trim()})";
        }
        return official;
    }

    private static readonly System.Text.RegularExpressions.Regex CollabSplit =
        new(@"\s*(?:&|\band\b|,|\+|\bwith\b)\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Artist folder: every album gets ONE clean album artist — the folder's artist ("Craig Padilla"), whatever the
    /// database or the old tags said ("Howard Givens", "Craig Padilla & Skip Murphy", "Craig Padilla and Skip Murphy").
    /// Collaborators stay in the track ARTIST tag, split into separate names ("Craig Padilla; Skip Murphy").
    /// "A &amp; B" is only split when one side is the folder's artist, so band names like "Simon &amp; Garfunkel" survive.
    /// </summary>
    public void UnifyFolderArtist(IEnumerable<Release> releases, string rootName)
    {
        var list = releases.Where(r => !r.Untouched && string.IsNullOrWhiteSpace(r.MoveToArtist) && r.AllTracks.Any()).ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(rootName)) return;
        var rootNorm = TextUtil.Normalize(rootName);

        List<string> Parts(string? s)
        {
            var parts = new List<string>();
            foreach (var p in TextUtil.SplitArtists(s))
            {
                var sub = CollabSplit.Split(p).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (sub.Count > 1 && sub.Any(x => TextUtil.Normalize(x) == rootNorm)) parts.AddRange(sub); else parts.Add(p.Trim());
            }
            return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The folder artist's spelling: the most common exact-match spelling in the tags/database, else the folder name.
        var canonical = list.SelectMany(r => r.AllTracks)
            .SelectMany(t => Parts(t.After.Artist).Concat(Parts(t.After.AlbumArtist)))
            .Where(x => TextUtil.Normalize(x) == rootNorm)
            .GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? rootName.Trim();

        foreach (var rel in list)
        {
            var before = rel.AlbumArtist;
            rel.AlbumArtist = canonical;
            foreach (var t in rel.AllTracks)
            {
                t.After.AlbumArtist = canonical;
                var parts = Parts(t.After.Artist).Select(x => TextUtil.Normalize(x) == rootNorm ? canonical : x).ToList();
                t.After.Artist = parts.Count == 0 ? canonical : JoinArtists(parts);
            }
            if (!string.Equals(before, canonical, StringComparison.Ordinal))
            {
                RunLog.Write("metadata", $"{rel.OriginalName}: album artist \"{before}\" → \"{canonical}\" (one album artist for the whole folder)");
                rel.AddFlag(Severity.Info, Stages.Metadata, $"Album artist set to \"{canonical}\" (was \"{before}\"); everyone involved stays in the track artist tag.");
            }
        }
    }

    private string JoinArtists(IEnumerable<string> artists) =>
        string.Join(_s.ArtistSeparator, artists.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));

    private static int DistinctTrackArtists(MatchResult m) =>
        m.Map.Values.Where(v => v != null).SelectMany(v => v!.Artists.Take(1)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public void WriteTags(Release rel)
    {
        foreach (var t in rel.AllTracks)
        {
            try
            {
                if (!t.TagsDiffer) continue; // already exactly right: don't touch the file
                TagIO.Write(t.Path, t.After, _s, t.AlbumTag);
            }
            catch (Exception e) { rel.AddFlag(Severity.Warning, Stages.Metadata, $"Couldn't write tags to {Path.GetFileName(t.Path)}: {e.Message}"); }
        }
    }
}
