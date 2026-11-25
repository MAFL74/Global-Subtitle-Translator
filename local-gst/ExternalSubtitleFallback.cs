using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Local_GST;

internal sealed record ExternalSubtitleOption(string Provider, string Release, string DownloadUrl, string ReferrerUrl, int MatchScore, string ProviderData = "")
{
    public override string ToString() => $"{Provider} · {Release} · match {MatchScore}%";
}
internal sealed record SubtitleDurationComparison(double MediaSeconds, double SubtitleSeconds)
{
    public double DifferenceSeconds => Math.Abs(MediaSeconds - SubtitleSeconds);
}

internal static class ExternalSubtitleFallback
{
    private sealed record Episode(string Title, int Season, int Number);

    public static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchAsync(
        string media, LocalGstSettings settings, Action<string> progress, CancellationToken cancellationToken, bool unattended = false)
    {
        var hasEpisode = TryEpisode(Path.GetFileNameWithoutExtension(media), out var episode);
        var results = new List<ExternalSubtitleOption>();

        if (settings.OpenSubtitlesEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.OpenSubtitlesApiKey))
                progress("OpenSubtitles er valgt, men API-nøkkel mangler. Legg den inn under Innstillinger.");
            else
                results.AddRange(await TryProviderAsync("OpenSubtitles", () => SearchOpenSubtitlesAsync(media, settings, progress, cancellationToken), progress, cancellationToken));
        }

        if (settings.SubDlEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.SubDlApiKey))
                progress("SubDL er valgt, men API-nøkkel mangler. Legg den inn under Innstillinger.");
            else
                results.AddRange(await TryProviderAsync("SubDL", () => SearchSubDlAsync(media, settings, progress, cancellationToken), progress, cancellationToken));
        }

        if (settings.SubSourceEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.SubSourceApiKey))
                progress("SubSource er valgt, men API-nøkkel mangler. Legg den inn under Innstillinger.");
            else
                results.AddRange(await TryProviderAsync("SubSource", () => SearchSubSourceAsync(media, settings, progress, cancellationToken), progress, cancellationToken));
        }

        if (settings.GestdownEnabled)
        {
            if (hasEpisode)
                results.AddRange(await TryProviderAsync("Gestdown", () => SearchGestdownAsync(media, episode, progress, cancellationToken), progress, cancellationToken));
            else
                progress("Gestdown støtter TV-episoder, ikke filmer.");
        }

        if (settings.ISubtitlesEnabled && !unattended)
            results.AddRange(await TryProviderAsync("iSubtitles", () => SearchISubtitlesAsync(media, progress, cancellationToken), progress, cancellationToken));

        foreach (var custom in settings.CustomProviders.Where(p => p.Enabled))
            results.AddRange(await TryProviderAsync(custom.Name, () => SearchCustomAsync(media, custom, progress, cancellationToken), progress, cancellationToken));

        if (!settings.Addic7edEnabled) return Sort(results);
        if (!hasEpisode)
        {
            progress("Addic7ed hopper over filen: TV-filnavnet mangler sesong/episodenummer.");
            return Sort(results);
        }

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalGST subtitle fallback/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
            var visitedEpisodePages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var query in SearchQueries(episode.Title))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress($"Søker i Addic7ed etter {episode.Title} S{episode.Season:00}E{episode.Number:00}…");
                var searchUrl = "https://www.addic7ed.com/search.php?search=" + Uri.EscapeDataString(query) + "&Submit=Search";
                using var search = await client.GetAsync(searchUrl, cancellationToken);
                if (!search.IsSuccessStatusCode) continue;
                var html = await search.Content.ReadAsStringAsync(cancellationToken);
                foreach (var page in FindEpisodePages(html, episode))
                {
                    if (!visitedEpisodePages.Add(page)) continue;
                    using var response = await client.GetAsync(new Uri(new Uri("https://www.addic7ed.com/"), page), cancellationToken);
                    if (!response.IsSuccessStatusCode) continue;
                    var episodeHtml = await response.Content.ReadAsStringAsync(cancellationToken);
                    results.AddRange(FindDownloads(episodeHtml, media,
                        new Uri(new Uri("https://www.addic7ed.com/"), page).ToString()));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException or RegexMatchTimeoutException)
        { progress($"Addic7ed-søk feilet: {ex.Message}"); }
        progress($"Addic7ed: {(results.Count == 0 ? "ingen treff" : $"fant {results.Count} treff")}");
        return Sort(results);
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> TryProviderAsync(
        string provider, Func<Task<IReadOnlyList<ExternalSubtitleOption>>> search, Action<string> progress, CancellationToken token)
    {
        try
        {
            var results = await search();
            progress($"{provider}: {(results.Count == 0 ? "ingen treff" : $"fant {results.Count} treff")}");
            return results;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException or TaskCanceledException)
        {
            progress($"{provider}-søk feilet: {ex.Message}");
            return [];
        }
    }

    public static async Task<string?> DownloadAsync(string media, ExternalSubtitleOption option, LocalGstSettings settings, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"LocalGST-{Guid.NewGuid():N}.srt");
        if (option.Provider == "OpenSubtitles")
            return await DownloadOpenSubtitlesAsync(media, destination, option, settings, cancellationToken);

        using var handler = new HttpClientHandler { AllowAutoRedirect = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalGST subtitle fallback/1.0");
        if (Uri.TryCreate(option.ReferrerUrl, UriKind.Absolute, out var referer)) client.DefaultRequestHeaders.Referrer = referer;
        if (option.Provider == "SubSource") client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", settings.SubSourceApiKey);
        if (option.Provider == "SubDL" && Uri.TryCreate(option.DownloadUrl, UriKind.Absolute, out var subdlUri) && subdlUri.Host.Equals("api.subdl.com", StringComparison.OrdinalIgnoreCase))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.SubDlApiKey);
        using var download = await client.GetAsync(option.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!download.IsSuccessStatusCode) return null;
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (option.Provider is "SubDL" or "SubSource" && IsJson(bytes))
        {
            using var json = JsonDocument.Parse(bytes);
            var url = json.RootElement.TryGetProperty("url", out var u) ? u :
                json.RootElement.TryGetProperty("downloadUrl", out u) ? u :
                json.RootElement.TryGetProperty("link", out u) ? u : default;
            if (url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
            {
                var finalUrl = Uri.TryCreate(url.GetString(), UriKind.Absolute, out var absolute)
                    ? absolute : new Uri(new Uri(option.Provider == "SubDL" ? "https://api.subdl.com/" : "https://api.subsource.net/"), url.GetString()!);
                using var finalResponse = await client.GetAsync(finalUrl, cancellationToken);
                if (!finalResponse.IsSuccessStatusCode) return null;
                bytes = await finalResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            }
        }
        if (bytes.Length > 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
            bytes = ExtractSrtFromZip(bytes);
        return await SaveValidatedSrtAsync(bytes, destination, cancellationToken);
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchOpenSubtitlesAsync(string media, LocalGstSettings settings, Action<string> progress, CancellationToken token)
    {
        progress("Søker i OpenSubtitles med filnavn og videohash …");
        using var client = CreateClient();
        var hash = TryMovieHash(media);
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = "en", ["query"] = Path.GetFileName(media).ToLowerInvariant(), ["type"] = TryEpisode(Path.GetFileNameWithoutExtension(media), out _) ? "episode" : "movie"
        };
        if (!string.IsNullOrWhiteSpace(hash)) parts["moviehash"] = hash;
        if (TryEpisode(Path.GetFileNameWithoutExtension(media), out var ep))
        { parts["season_number"] = ep.Season.ToString(); parts["episode_number"] = ep.Number.ToString(); }
        var query = string.Join("&", parts.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.opensubtitles.com/api/v1/subtitles?" + query);
        request.Headers.Add("Api-Key", settings.OpenSubtitlesApiKey);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) { progress($"OpenSubtitles svarte HTTP {(int)response.StatusCode}."); return []; }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var results = new List<ExternalSubtitleOption>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var attr)) continue;
            var language = ReadString(attr, "language")?.ToLowerInvariant();
            if (language is not ("en" or "eng" or "english")) continue;
            if (!attr.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
            var file = files[0];
            var fileId = ReadString(file, "file_id");
            if (string.IsNullOrWhiteSpace(fileId)) continue;
            var release = ReadString(attr, "release") ?? ReadString(file, "file_name") ?? "OpenSubtitles release";
            var score = ReleaseMatchScore(release, media);
            if (ReadBool(attr, "moviehash_match") || ReadBool(item, "moviehash_match")) score = Math.Min(100, score + 40);
            results.Add(new ExternalSubtitleOption("OpenSubtitles", release, "", "", score, fileId));
        }
        return results;
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchSubDlAsync(string media, LocalGstSettings settings, Action<string> progress, CancellationToken token)
    {
        progress("Søker i SubDL etter filnavn og releasetype …");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.SubDlApiKey);
        var url = "https://api.subdl.com/api/v2/files/search?filename=" + Uri.EscapeDataString(Path.GetFileName(media)) + "&languages=en&subs_per_page=30";
        using var response = await client.GetAsync(url, token);
        if (!response.IsSuccessStatusCode) { progress($"SubDL svarte HTTP {(int)response.StatusCode}."); return []; }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!document.RootElement.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array) return [];
        var results = new List<ExternalSubtitleOption>();
        foreach (var sub in subs.EnumerateArray())
        {
            var language = (ReadString(sub, "lang") ?? ReadString(sub, "language") ?? "").ToLowerInvariant();
            if (language is not ("en" or "eng" or "english")) continue;
            var release = ReadString(sub, "release_name") ?? ReadString(sub, "release") ?? "SubDL release";
            var direct = ReadString(sub, "url");
            var id = ReadString(sub, "n_id") ?? ReadString(sub, "nId") ?? ReadString(sub, "id");
            if (string.IsNullOrWhiteSpace(direct) && string.IsNullOrWhiteSpace(id)) continue;
            var downloadUrl = !string.IsNullOrWhiteSpace(direct)
                ? (Uri.TryCreate(direct, UriKind.Absolute, out _) ? direct : new Uri(new Uri("https://api.subdl.com/"), direct).ToString())
                : $"https://api.subdl.com/api/v2/subtitles/{Uri.EscapeDataString(id!)}/download?format=file";
            var score = ReadDouble(sub, "match_score") is { } confidence
                ? (int)Math.Clamp(confidence * 100, 0, 100)
                : ReleaseMatchScore(release, media);
            results.Add(new ExternalSubtitleOption("SubDL", release, downloadUrl, "", score));
        }
        return results;
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchSubSourceAsync(string media, LocalGstSettings settings, Action<string> progress, CancellationToken token)
    {
        progress("Søker i SubSource etter tittel og engelsk tekst …");
        using var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", settings.SubSourceApiKey);
        var isEpisode = TryEpisode(Path.GetFileNameWithoutExtension(media), out var episode);
        var title = isEpisode ? episode.Title : Path.GetFileNameWithoutExtension(media);
        var searchUrl = "https://api.subsource.net/api/v1/movies/search?searchType=text&q=" + Uri.EscapeDataString(title);
        if (isEpisode) searchUrl += "&season=" + episode.Season;
        using var search = await client.GetAsync(searchUrl, token);
        if (!search.IsSuccessStatusCode) { progress($"SubSource tittelsøk svarte HTTP {(int)search.StatusCode}."); return []; }
        using var titlesDoc = JsonDocument.Parse(await search.Content.ReadAsStringAsync(token));
        var titleItems = GetArray(titlesDoc.RootElement, "data", "results");
        if (titleItems.ValueKind != JsonValueKind.Array) return [];
        var titleId = titleItems.EnumerateArray()
            .Where(item => ReadString(item, "movieId") is not null)
            .OrderByDescending(item => Similarity(ReadString(item, "title") ?? "", title))
            .Select(item => ReadString(item, "movieId")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(titleId)) return [];
        var subUrl = "https://api.subsource.net/api/v1/subtitles?language=english&limit=100&movieId=" + Uri.EscapeDataString(titleId);
        if (isEpisode) subUrl += "&seasonNumber=" + episode.Season + "&episodeNumber=" + episode.Number;
        using var subtitles = await client.GetAsync(subUrl, token);
        if (!subtitles.IsSuccessStatusCode) { progress($"SubSource undertekstsøk svarte HTTP {(int)subtitles.StatusCode}."); return []; }
        using var subsDoc = JsonDocument.Parse(await subtitles.Content.ReadAsStringAsync(token));
        var subs = GetArray(subsDoc.RootElement, "data", "results");
        if (subs.ValueKind != JsonValueKind.Array) return [];
        var results = new List<ExternalSubtitleOption>();
        foreach (var sub in subs.EnumerateArray())
        {
            var language = (ReadString(sub, "language") ?? "").ToLowerInvariant();
            if (language is not ("english" or "en" or "eng")) continue;
            var release = ReadRelease(sub, "releaseInfo") ?? "SubSource release";
            var id = ReadString(sub, "subtitleId");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var url = "https://api.subsource.net/api/v1/subtitles/" + Uri.EscapeDataString(id) + "/download";
            results.Add(new ExternalSubtitleOption("SubSource", release, url, "", ReleaseMatchScore(release, media), id));
        }
        return results;
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchGestdownAsync(string media, Episode episode, Action<string> progress, CancellationToken token)
    {
        progress($"Søker i Gestdown etter {episode.Title} S{episode.Season:00}E{episode.Number:00}…");
        using var client = CreateClient();
        var showUrl = "https://api.gestdown.info/shows/search/" + Uri.EscapeDataString(episode.Title);
        using var showResponse = await client.GetAsync(showUrl, token);
        if (!showResponse.IsSuccessStatusCode) { progress($"Gestdown seriesøk svarte HTTP {(int)showResponse.StatusCode}."); return []; }
        using var showDoc = JsonDocument.Parse(await showResponse.Content.ReadAsStringAsync(token));
        var shows = GetArray(showDoc.RootElement, "shows", "data");
        if (shows.ValueKind != JsonValueKind.Array) return [];
        var showId = shows.EnumerateArray()
            .Where(s => ReadString(s, "id") is not null)
            .OrderByDescending(s => Similarity(ReadString(s, "name") ?? ReadString(s, "title") ?? "", episode.Title))
            .Select(s => ReadString(s, "id")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(showId)) return [];
        var subsUrl = $"https://api.gestdown.info/subtitles/get/{Uri.EscapeDataString(showId)}/{episode.Season}/{episode.Number}/en";
        using var subsResponse = await client.GetAsync(subsUrl, token);
        if (subsResponse.StatusCode == HttpStatusCode.TooManyRequests) { progress("Gestdown begrenset søkefrekvensen (HTTP 429)."); return []; }
        if (!subsResponse.IsSuccessStatusCode) { progress($"Gestdown episode-søk svarte HTTP {(int)subsResponse.StatusCode}."); return []; }
        using var subsDoc = JsonDocument.Parse(await subsResponse.Content.ReadAsStringAsync(token));
        var subs = GetArray(subsDoc.RootElement, "matchingSubtitles", "data");
        if (subs.ValueKind != JsonValueKind.Array) return [];
        return subs.EnumerateArray().Where(s => ReadBool(s, "completed"))
            .Select(s =>
            {
                var download = ReadString(s, "downloadUri") ?? "";
                if (!Uri.TryCreate(new Uri("https://api.gestdown.info/"), download.TrimStart('/'), out var uri)) return null;
                var release = ReadString(s, "version") ?? "Gestdown English";
                return new ExternalSubtitleOption("Gestdown", release, uri.ToString(), "", ReleaseMatchScore(release, media));
            }).Where(x => x is not null).Cast<ExternalSubtitleOption>().ToArray();
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchISubtitlesAsync(string media, Action<string> progress, CancellationToken token)
    {
        var isEpisode = TryEpisode(Path.GetFileNameWithoutExtension(media), out var episode);
        var query = isEpisode ? $"{episode.Title} S{episode.Season:00}E{episode.Number:00}" : Path.GetFileNameWithoutExtension(media);
        progress($"Søker i iSubtitles etter {query}…");
        using var client = CreateClient();
        using var response = await client.GetAsync("https://isubtitles.org/search?kwd=" + Uri.EscapeDataString(query), token);
        if (!response.IsSuccessStatusCode) { progress($"iSubtitles svarte HTTP {(int)response.StatusCode}."); return []; }
        var html = await response.Content.ReadAsStringAsync(token);
        var pattern = new Regex("href\\s*=\\s*['\\\"](?<path>/[^'\\\"<>\\s]+/(?:english|english-subtitles)/(?<id>\\d+))['\\\"]", RegexOptions.IgnoreCase);
        var results = new List<ExternalSubtitleOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in pattern.Matches(html))
        {
            var relative = WebUtility.HtmlDecode(match.Groups["path"].Value);
            if (!seen.Add(relative)) continue;
            var titlePath = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "English subtitle";
            var release = Regex.Replace(titlePath.Replace('-', ' '), @"\\s+", " ").Trim();
            var downloadPath = "/download/" + relative.TrimStart('/');
            results.Add(new ExternalSubtitleOption("iSubtitles", release,
                new Uri(new Uri("https://isubtitles.org/"), downloadPath.TrimStart('/')).ToString(),
                "https://isubtitles.org/search?kwd=" + Uri.EscapeDataString(query), ReleaseMatchScore(release, media)));
        }
        return results;
    }

    private static JsonElement GetArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Array) return nested;
            }
        return default;
    }

    private static string? ReadRelease(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array) return string.Join(", ", value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        return null;
    }

    private static async Task<IReadOnlyList<ExternalSubtitleOption>> SearchCustomAsync(string media, CustomSubtitleProvider provider, Action<string> progress, CancellationToken token)
    {
        progress($"Søker i {provider.Name} …");
        var episodeQuery = TryEpisode(Path.GetFileNameWithoutExtension(media), out var episode)
            ? $"{episode.Title} S{episode.Season:00}E{episode.Number:00}" : Path.GetFileNameWithoutExtension(media);
        var url = provider.SearchUrlTemplate.Replace("{query}", Uri.EscapeDataString(episodeQuery), StringComparison.OrdinalIgnoreCase)
            .Replace("{filename}", Uri.EscapeDataString(Path.GetFileName(media)), StringComparison.OrdinalIgnoreCase)
            .Replace("{language}", "en", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        { progress($"Hopper over {provider.Name}: ugyldig URL-mal."); return []; }
        using var client = CreateClient();
        if (!string.IsNullOrWhiteSpace(provider.ApiKey) && !string.IsNullOrWhiteSpace(provider.ApiKeyHeader))
            client.DefaultRequestHeaders.TryAddWithoutValidation(provider.ApiKeyHeader, provider.ApiKey);
        using var response = await client.GetAsync(uri, token);
        if (!response.IsSuccessStatusCode) { progress($"{provider.Name} svarte HTTP {(int)response.StatusCode}."); return []; }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("results", out var array) ? array : default;
        if (items.ValueKind != JsonValueKind.Array) { progress($"{provider.Name}: forventet JSON results-liste."); return []; }
        return items.EnumerateArray().Select(item =>
        {
            var language = (ReadString(item, "language") ?? "").ToLowerInvariant();
            var release = ReadString(item, "release") ?? "Egen tjeneste";
            var download = ReadString(item, "download_url") ?? "";
            if (language is not ("en" or "eng" or "english") || !Uri.TryCreate(download, UriKind.Absolute, out _)) return null;
            var score = ReadDouble(item, "match_score") is { } s ? (int)Math.Clamp(s <= 1 ? s * 100 : s, 0, 100)
                : ReleaseMatchScore(release, media);
            return new ExternalSubtitleOption(provider.Name, release, download, "", score);
        }).Where(x => x is not null).Cast<ExternalSubtitleOption>().ToArray();
    }

    private static async Task<string?> DownloadOpenSubtitlesAsync(string media, string destination, ExternalSubtitleOption option, LocalGstSettings settings, CancellationToken token)
    {
        using var client = CreateClient();
        var apiBase = "https://api.opensubtitles.com/api/v1/";
        string? userToken = null;
        if (!string.IsNullOrWhiteSpace(settings.OpenSubtitlesUsername) && !string.IsNullOrWhiteSpace(settings.OpenSubtitlesPassword))
        {
            using var login = new HttpRequestMessage(HttpMethod.Post, apiBase + "login")
            { Content = System.Net.Http.Json.JsonContent.Create(new { username = settings.OpenSubtitlesUsername, password = settings.OpenSubtitlesPassword }) };
            login.Headers.Add("Api-Key", settings.OpenSubtitlesApiKey);
            using var loginResponse = await client.SendAsync(login, token);
            if (loginResponse.IsSuccessStatusCode)
            {
                using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(token));
                userToken = ReadString(loginJson.RootElement, "token");
                apiBase = ReadString(loginJson.RootElement, "base_url") ?? apiBase;
                if (!apiBase.EndsWith('/')) apiBase += "/";
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(apiBase), "download"))
        { Content = System.Net.Http.Json.JsonContent.Create(new { file_id = int.Parse(option.ProviderData) }) };
        request.Headers.Add("Api-Key", settings.OpenSubtitlesApiKey);
        if (!string.IsNullOrWhiteSpace(userToken)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("link", out var link) || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)) return null;
        var bytes = await client.GetByteArrayAsync(uri, token);
        return await SaveValidatedSrtAsync(bytes, destination, token);
    }

    private static async Task<string?> SaveValidatedSrtAsync(byte[] bytes, string destination, CancellationToken token)
    {
        if (File.Exists(destination)) return IsValidSrt(destination) ? destination : null;
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)) return null;
        var temp = Path.Combine(Path.GetTempPath(), $"LocalGST-{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), token);
            if (!IsValidSrt(temp)) return null;
            File.Move(temp, destination);
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalGST/1.0 subtitle search");
        return client;
    }

    private static bool IsJson(byte[] bytes) => bytes.Length > 0 && bytes[0] is (byte)'{' or (byte)'[';
    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number ? value.ToString() : null;
    private static bool ReadBool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static double? ReadDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    private static IReadOnlyList<ExternalSubtitleOption> Sort(IEnumerable<ExternalSubtitleOption> options) => options.OrderByDescending(x => x.MatchScore).ThenBy(x => x.Release, StringComparer.OrdinalIgnoreCase).ToArray();

    private static byte[] ExtractSrtFromZip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("ZIP-arkivet inneholder ingen SRT-fil.");
        using var source = entry.Open(); using var output = new MemoryStream(); source.CopyTo(output); return output.ToArray();
    }

    private static string? TryMovieHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 65536) return null;
            ulong hash = (ulong)stream.Length;
            var buffer = new byte[65536];
            stream.ReadExactly(buffer);
            for (var i = 0; i < buffer.Length; i += 8) hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
            stream.Seek(-65536, SeekOrigin.End); stream.ReadExactly(buffer);
            for (var i = 0; i < buffer.Length; i += 8) hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
            return hash.ToString("x16");
        }
        catch (IOException) { return null; }
    }

    private static IEnumerable<string> FindEpisodePages(string html, Episode episode)
    {
        var pattern = new Regex("<a\\b[^>]*href\\s*=\\s*['\\\"](?<href>/?serie/[^'\\\"]+)['\\\"][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var expectedCode = $"{episode.Season:00}x{episode.Number:00}";
        return pattern.Matches(html).Cast<Match>().Select(m =>
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            var parts = href.TrimStart('/').Split('/');
            var show = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]).Replace('_', ' ') : "";
            var text = Regex.Replace(m.Groups["text"].Value, "<[^>]+>", " ");
            return new { Href = href, Parts = parts, Show = show, Text = text };
        })
        .Where(x => x.Parts.Length > 3 && int.TryParse(x.Parts[2], out var s) && s == episode.Season &&
                    int.TryParse(x.Parts[3], out var n) && n == episode.Number)
        .OrderByDescending(x => Similarity(x.Show, episode.Title) + (x.Text.Contains(expectedCode, StringComparison.OrdinalIgnoreCase) ? 10 : 0))
        .Select(x => x.Href);
    }

    private static IEnumerable<ExternalSubtitleOption> FindDownloads(string html, string media, string referrer)
    {
        var pattern = new Regex("<td[^>]*class\\s*=\\s*['\\\"]language['\\\"][^>]*>\\s*English\\b.*?<b>\\s*Completed\\s*</b>.*?href\\s*=\\s*['\\\"](?<href>/original/\\d+/\\d+)['\\\"]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in pattern.Matches(html))
        {
            var prefix = html[..match.Index];
            var i = prefix.LastIndexOf("Version ", StringComparison.OrdinalIgnoreCase);
            var release = i >= 0 ? Regex.Match(prefix[i..], @"Version\s+(?<r>[^,<]+)", RegexOptions.IgnoreCase).Groups["r"].Value.Trim() : "English · release ikke oppgitt";
            var mediaName = Path.GetFileNameWithoutExtension(media);
            var score = ReleaseMatchScore(release, mediaName);
            yield return new ExternalSubtitleOption("Addic7ed", release,
                new Uri(new Uri("https://www.addic7ed.com/"), match.Groups["href"].Value).ToString(), referrer, score);
        }
    }

    private static IEnumerable<string> SearchQueries(string title)
    {
        yield return title;
        var words = Regex.Split(title, @"[^A-Za-z0-9]+").Where(w => w.Length > 1).ToArray();
        if (words.Length > 2) yield return string.Join(' ', words.Take(2));
    }

    private static bool TryEpisode(string name, out Episode episode)
    {
        var match = Regex.Match(name, @"(?<title>.+?)[ ._-]S(?<season>\d{1,2})E(?<episode>\d{1,2})(?:[ ._-]|$)", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(name, @"(?<title>.+?)[ ._-](?<season>\d{1,2})x(?<episode>\d{1,2})(?:[ ._-]|$)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["season"].Value, out var season) || !int.TryParse(match.Groups["episode"].Value, out var number))
        { episode = new Episode("", 0, 0); return false; }
        var title = Regex.Replace(match.Groups["title"].Value.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        episode = new Episode(title, season, number);
        return title.Length > 0;
    }

    private static int Similarity(string a, string b)
    {
        static HashSet<string> Tokens(string s) => Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+").Where(x => x.Length > 1)
            .Where(x => !Regex.IsMatch(x, @"^(720p|1080p|2160p|web|webrip|webdl|bluray|hdtv|x264|x265|h264|h265|hevc|proper|repack|aac|ddp|dts)$"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = Tokens(a); var right = Tokens(b);
        if (left.Count == 0) return 0;
        return (int)(100.0 * left.Intersect(right).Count() / left.Count);
    }

    private static int ReleaseTypeScore(string release, string media)
    {
        var left = ReleaseTypes(release); var right = ReleaseTypes(media); var score = 0;
        foreach (var family in left.Keys.Union(right.Keys))
        {
            left.TryGetValue(family, out var wanted); right.TryGetValue(family, out var actual);
            if (wanted is not null && actual is not null) score += wanted == actual ? 25 : -20;
        }
        return score;
    }

    private static int ReleaseMatchScore(string release, string media)
    {
        var score = 50; // Neutral when the provider does not expose enough release metadata.
        var titleSimilarity = Similarity(release, Path.GetFileNameWithoutExtension(media));
        if (titleSimilarity > 0) score += titleSimilarity / 2;
        score += ReleaseTypeScore(release, media);
        return Math.Clamp(score, 0, 100);
    }

    private static Dictionary<string, string> ReleaseTypes(string value)
    {
        var t = Regex.Split(value.ToLowerInvariant(), @"[^a-z0-9]+");
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        if (t.Contains("webrip")) r["source"] = "webrip";
        else if (t.Contains("webdl") || t.Zip(t.Skip(1), (a, b) => a == "web" && b == "dl").Any(x => x)) r["source"] = "webdl";
        else if (t.Any(x => x is "bluray" or "bdrip" or "brrip")) r["source"] = "bluray";
        else if (t.Any(x => x is "hdtv" or "pdtv")) r["source"] = "hdtv";
        if (t.Any(x => x is "hevc" or "x265" or "h265")) r["video"] = "hevc";
        else if (t.Any(x => x is "x264" or "h264" or "avc")) r["video"] = "h264";
        if (t.Any(x => x is "2160p" or "4k")) r["resolution"] = "2160";
        else if (t.Contains("1080p")) r["resolution"] = "1080";
        else if (t.Contains("720p")) r["resolution"] = "720";
        return r;
    }

    private static bool IsValidSrt(string path) => Regex.IsMatch(File.ReadAllText(path),
        @"(?m)^\s*\d+\s*\r?\n\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,.]\d{3}");

    public static bool IsValidSubtitleFile(string path) => File.Exists(path) && IsValidSrt(path);

    public static SubtitleDurationComparison? CompareDuration(string media, string subtitle)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe is null) return null;
        try
        {
            var start = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", media }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000)) { process.Kill(entireProcessTree: true); return null; }
            if (process.ExitCode != 0 || !double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mediaSeconds)) return null;
            var text = File.ReadAllText(subtitle);
            var matches = Regex.Matches(text, @"(?m)\d{2,}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*(?<end>\d{2,}:\d{2}:\d{2}[,.]\d{3})");
            if (matches.Count == 0) return null;
            var end = matches.Cast<Match>().Select(m => ParseTimestamp(m.Groups["end"].Value)).Max();
            return new SubtitleDurationComparison(mediaSeconds, end);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException) { return null; }
    }

    private static double ParseTimestamp(string value)
    {
        var p = value.Replace(',', '.').Split(':');
        return double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) * 3600
            + double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) * 60
            + double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindFfprobe()
    {
        var jellyfin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin", "Server", "ffprobe.exe");
        if (File.Exists(jellyfin)) return jellyfin;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, "ffprobe.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
