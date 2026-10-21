using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles.Providers;

/// <summary>
/// SubDL (https://subdl.com/api-doc). Busca pelo IMDb ID (ou TMDb ID); sem hash. Episódios sem resultado
/// caem para a busca da temporada, que traz pacotes (o episódio é extraído do ZIP ou do arquivo já separado
/// pelo servidor, <c>unpack=1</c>). A chave vai na URL: nunca registrar a URL completa.
/// </summary>
public sealed partial class SubDlProvider(HttpClient http, Func<SubDlSettings> settings, MetadataCache? cache = null) : ISubtitleProvider
{
    public const string ProviderName = "SubDL";
    public const string ApiUrl = "https://api.subdl.com/api/v1/subtitles";
    public const string DownloadUrl = "https://dl.subdl.com";

    private const int PageSize = 30;

    public string Name => ProviderName;

    public bool IsConfigured => settings() is { Enabled: true, ApiKey.Length: > 0 };

    /// <summary>O SubDL não informa a cota restante.</summary>
    public int? RemainingDownloads => null;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        var languages = string.Join(',', query.Languages.Select(l => LanguageCode(l.Code)).Distinct().Order(StringComparer.Ordinal));
        var id = query.ImdbId is { Length: > 0 } imdb ? ("imdb_id", imdb) : ("tmdb_id", query.TmdbId.ToString(CultureInfo.InvariantCulture));
        List<(string, string)> common = [id, ("languages", languages), ("releases", "1"), ("hi", "1"), ("unpack", "1"), ("subs_per_page", PageSize.ToString(CultureInfo.InvariantCulture))];

        if (query.Kind == MediaKind.Movie)
        {
            var items = await SearchCoreAsync([("type", "movie"), .. common], query.Refresh, ct).ConfigureAwait(false);
            return Parse(items, query, fromSeasonSearch: false);
        }

        var season = query.Season?.ToString(CultureInfo.InvariantCulture) ?? "1";
        var episode = query.Episode?.ToString(CultureInfo.InvariantCulture) ?? "1";
        var found = Parse(await SearchCoreAsync([("type", "tv"), .. common, ("season_number", season), ("episode_number", episode)], query.Refresh, ct)
            .ConfigureAwait(false), query, fromSeasonSearch: false);
        if (found.Count > 0) return found;

        // Pacotes da temporada inteira não aparecem na busca por episódio.
        return Parse(await SearchCoreAsync([("type", "tv"), .. common, ("season_number", season)], query.Refresh, ct).ConfigureAwait(false),
            query, fromSeasonSearch: true);
    }

    private async Task<List<JsonElement>> SearchCoreAsync(List<(string Name, string Value)> parameters, bool refresh, CancellationToken ct)
    {
        var queryString = string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        var cacheKey = $"{ProviderName}:{queryString}"; // sem a chave
        var json = refresh ? null : cache?.Get(cacheKey);

        if (json is null)
        {
            var url = $"{ApiUrl}?api_key={Uri.EscapeDataString(settings().ApiKey?.Trim() ?? "")}&{queryString}";
            using var response = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), Name, ct).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound) ThrowIfError(response.StatusCode, json, download: false);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            cache?.Set(cacheKey, json);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // "status": false + "error": "can't find movie or tv" = nenhum resultado.
        if (root.Prop("status") is { ValueKind: JsonValueKind.False }) return [];
        return [.. root.Items("subtitles").Select(e => e.Clone())];
    }

    private List<SubtitleCandidate> Parse(List<JsonElement> items, SubtitleQuery query, bool fromSeasonSearch)
    {
        var result = new List<SubtitleCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (FromCode(item.Str("language")) is not { } code || item.Str("url") is not { Length: > 0 } url) continue;
            var releases = item.Items("releases").Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : r.Str("release_name"))
                .OfType<string>().ToList();
            var texts = releases.Append(item.Str("name") ?? "").Append(item.Str("release_name") ?? "").Append(item.Str("comment") ?? "");
            var forced = texts.Any(t => ForcedRegex().IsMatch(t));
            var language = new SubtitleLanguage(code, forced, item.Bool("hi"));
            if (!query.Languages.Any(l => l.SameKind(language))) continue;

            var isPack = false;
            if (query.Kind == MediaKind.Series)
            {
                var (ok, pack, direct) = MatchEpisode(item, query);
                if (!ok) continue;
                isPack = pack && direct is null;
                if (direct is not null) url = direct;
                if (fromSeasonSearch && !pack && direct is null) continue;
            }

            if (!seen.Add(url)) continue;
            result.Add(new SubtitleCandidate(
                Name,
                url,
                language,
                releases.FirstOrDefault() ?? item.Str("release_name") ?? item.Str("name"),
                HashMatch: false,
                IdMatch: true,
                isPack,
                item.Int("downloads") ?? 0,
                Path.GetExtension(url) is { Length: > 1 } ext && !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ? ext[1..].ToLowerInvariant() : "srt",
                item.Bool("ai_translated")));
        }

        return result;
    }

    /// <summary>
    /// O item serve para o episódio? Episódio único (episode_from = episode_end = alvo), intervalo que o contém,
    /// ou temporada inteira. Em pacotes, prefere o arquivo já separado pelo servidor (unpack_files).
    /// </summary>
    private static (bool Ok, bool Pack, string? Direct) MatchEpisode(JsonElement item, SubtitleQuery query)
    {
        var target = query.Episode ?? 0;
        if (item.Int("season") is { } s && s > 0 && query.Season is { } season && s != season) return (false, false, null);

        var from = item.Int("episode_from");
        var end = item.Int("episode_end");
        var fullSeason = item.Bool("full_season");
        var hasRange = from is > 0 && end is > 0 && from != end;
        if (hasRange && (target < from || target > end)) return (false, false, null);

        if (!hasRange && !fullSeason)
        {
            var single = item.Int("episode") is > 0 ? item.Int("episode") : from;
            return (single is null or 0 || single == target, false, null);
        }

        var direct = item.Items("unpack_files")
            .FirstOrDefault(f => f.Int("episode") == target ||
                                 Parsing.ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(f.Str("name") ?? "")) is { } p && p.Episode == target);
        return (true, true, direct.ValueKind == JsonValueKind.Object ? direct.Str("url") : null);
    }

    public async Task<SubtitleFile> DownloadAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct)
    {
        var url = candidate.ProviderId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? candidate.ProviderId
            : DownloadUrl + (candidate.ProviderId.StartsWith('/') ? "" : "/") + candidate.ProviderId;

        using var response = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), Name, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            ThrowIfError(response.StatusCode, System.Text.Encoding.UTF8.GetString(content), download: true);

        return SubtitleArchive.IsZip(content)
            ? SubtitleArchive.Extract(content, query.Season, query.Kind == MediaKind.Series ? query.Episode : null, Name)
            : new SubtitleFile(content, candidate.Format);
    }

    /// <summary>Testa a chave com uma busca mínima (para o botão "Testar").</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        await SearchCoreAsync([("type", "tv"), ("imdb_id", "tt0944947"), ("season_number", "1"), ("episode_number", "1"), ("languages", "EN"), ("subs_per_page", "1")],
            refresh: true, ct).ConfigureAwait(false);
        return "Chave válida.";
    }

    /// <summary>"pt-BR" → "BR_PT"; "en" → "EN".</summary>
    internal static string LanguageCode(string code) =>
        code.Equals(SubtitleLanguage.PortugueseBrazil, StringComparison.OrdinalIgnoreCase) ? "BR_PT" : code.Split('-')[0].ToUpperInvariant();

    internal static string? FromCode(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        "BR_PT" => SubtitleLanguage.PortugueseBrazil,
        var other when other.Contains('_', StringComparison.Ordinal) => null, // variantes regionais sem equivalente
        var other => SubtitleLanguages.Normalize(other),
    };

    private void ThrowIfError(HttpStatusCode status, string body, bool download)
    {
        if ((int)status is >= 200 and < 300) return;

        string error = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            error = (document.RootElement.Str("error") ?? document.RootElement.Str("message") ?? "").ToLowerInvariant();
        }
        catch (JsonException)
        {
        }

        throw status switch
        {
            HttpStatusCode.TooManyRequests when error.Contains("daily", StringComparison.Ordinal) || error.Contains("download_limit", StringComparison.Ordinal) ||
                                                error.Contains("limit_exceeded", StringComparison.Ordinal) =>
                new SubtitleProviderException(Name, SubtitleProviderError.QuotaExceeded, "Cota diária do SubDL esgotada."),
            HttpStatusCode.TooManyRequests => new SubtitleProviderException(Name, SubtitleProviderError.RateLimited,
                "Muitas requisições ao SubDL em pouco tempo. Tente de novo em instantes."),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized when error.Contains("key", StringComparison.Ordinal) || error.Contains("auth", StringComparison.Ordinal) ||
                                                                         status == HttpStatusCode.Unauthorized =>
                new SubtitleProviderException(Name, SubtitleProviderError.AuthenticationFailed, "A chave do SubDL foi recusada. Verifique nas Configurações."),
            HttpStatusCode.PaymentRequired => new SubtitleProviderException(Name, SubtitleProviderError.Unavailable, "Este recurso do SubDL exige assinatura paga."),
            HttpStatusCode.NotFound when download => new SubtitleProviderException(Name, SubtitleProviderError.InvalidContent, "A legenda não existe mais no SubDL."),
            _ => new SubtitleProviderException(Name, SubtitleProviderError.Unavailable, $"SubDL respondeu HTTP {(int)status}."),
        };
    }

    [GeneratedRegex(@"\bforced\b|foreign\s+parts?", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedRegex();
}
