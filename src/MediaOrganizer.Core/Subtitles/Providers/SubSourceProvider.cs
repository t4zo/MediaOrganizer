using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles.Providers;

/// <summary>
/// SubSource (api.subsource.net). Busca em dois passos: o título pelo IMDb ID (nas séries, cada temporada
/// é um título próprio) e depois as legendas desse título, um idioma por requisição. Download em ZIP.
/// A chave vai na URL: nunca registrar a URL completa.
/// </summary>
public sealed partial class SubSourceProvider(HttpClient http, Func<SubSourceSettings> settings, MetadataCache? cache = null) : ISubtitleProvider
{
    public const string ProviderName = "SubSource";
    public const string ApiUrl = "https://api.subsource.net/api/v1/";

    /// <summary>Limite que vira "cota esgotada" (o site tem janelas por minuto, hora e dia).</summary>
    private static readonly TimeSpan QuotaThreshold = TimeSpan.FromMinutes(30);

    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["pt-BR"] = "brazilian_portuguese", ["pt"] = "portuguese", ["en"] = "english", ["es"] = "spanish", ["fr"] = "french",
        ["de"] = "german", ["it"] = "italian", ["nl"] = "dutch", ["sv"] = "swedish", ["da"] = "danish", ["no"] = "norwegian",
        ["fi"] = "finnish", ["pl"] = "polish", ["cs"] = "czech", ["hu"] = "hungarian", ["ro"] = "romanian", ["tr"] = "turkish",
        ["el"] = "greek", ["ru"] = "russian", ["uk"] = "ukrainian", ["ar"] = "arabic", ["he"] = "hebrew", ["ja"] = "japanese",
        ["ko"] = "korean", ["zh"] = "chinese", ["hi"] = "hindi", ["th"] = "thai", ["vi"] = "vietnamese", ["id"] = "indonesian",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> Codes =
        Names.ToFrozenDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public string Name => ProviderName;

    public bool IsConfigured => settings() is { Enabled: true, ApiKey.Length: > 0 };

    public int? RemainingDownloads => null;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ImdbId)) return [];
        if (await FindTitleAsync(query, ct).ConfigureAwait(false) is not { } titleId) return [];

        var result = new List<SubtitleCandidate>();
        foreach (var language in query.Languages.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Names.TryGetValue(language, out var name)) continue;
            List<(string, string)> parameters = [("language", name), ("limit", "100"), ("movieId", titleId)];
            if (query.Kind == MediaKind.Series)
            {
                parameters.Add(("seasonNumber", (query.Season ?? 1).ToString(CultureInfo.InvariantCulture)));
                parameters.Add(("episodeNumber", (query.Episode ?? 1).ToString(CultureInfo.InvariantCulture)));
            }

            using var document = await GetJsonAsync("subtitles", parameters, query.Refresh, ct).ConfigureAwait(false);
            if (document.RootElement.Prop("success") is { ValueKind: JsonValueKind.False }) continue;
            result.AddRange(Parse(document.RootElement, query));
        }
        return result;
    }

    /// <summary>ID do título no SubSource (o da temporada, para séries), em cache junto com as buscas.</summary>
    private async Task<string?> FindTitleAsync(SubtitleQuery query, CancellationToken ct)
    {
        List<(string, string)> parameters = [("searchType", "imdb"), ("imdb", query.ImdbId!)];
        if (query.Kind == MediaKind.Series) parameters.Add(("season", (query.Season ?? 1).ToString(CultureInfo.InvariantCulture)));

        using var document = await GetJsonAsync("movies/search", parameters, query.Refresh, ct).ConfigureAwait(false);
        var titles = document.RootElement.Items("data").ToList();
        var title = query.Kind == MediaKind.Series
            ? titles.FirstOrDefault(t => t.Int("season") == query.Season) is { ValueKind: JsonValueKind.Object } s ? s : titles.FirstOrDefault()
            : titles.FirstOrDefault();
        return title.ValueKind == JsonValueKind.Object ? title.Str("movieId") : null;
    }

    private IEnumerable<SubtitleCandidate> Parse(JsonElement root, SubtitleQuery query)
    {
        foreach (var item in root.Items("data"))
        {
            if (item.Str("subtitleId") is not { Length: > 0 } id) continue;
            if (!Codes.TryGetValue(item.Str("language") ?? "", out var code)) continue;

            var releases = item.Items("releaseInfo").Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList();
            var commentary = item.Str("commentary") ?? "";
            var forced = item.Bool("foreignParts") || ForcedRegex().IsMatch(commentary);
            var hearingImpaired = item.Bool("hearingImpaired") || (!NonHearingImpairedRegex().IsMatch(commentary) && HearingImpairedRegex().IsMatch(commentary));
            var language = new SubtitleLanguage(code, forced, hearingImpaired);
            if (!query.Languages.Any(l => l.SameKind(language))) continue;

            var isPack = false;
            if (query.Kind == MediaKind.Series)
            {
                var parsed = releases.Select(ReleaseNameParser.ParseEpisode).OfType<ParsedEpisode>().FirstOrDefault();
                var seasonPack = releases.Any(r => SeasonOnlyRegex().IsMatch(r));
                if (parsed is not null)
                {
                    if (parsed.Season != query.Season || query.Episode < parsed.Episode || query.Episode > (parsed.LastEpisode ?? parsed.Episode)) continue;
                    isPack = parsed.LastEpisode is not null && parsed.LastEpisode != parsed.Episode;
                }
                else
                {
                    isPack = seasonPack;
                }
            }

            yield return new SubtitleCandidate(Name, id, language, releases.FirstOrDefault(), HashMatch: false, IdMatch: true, isPack,
                item.Int("downloads") ?? 0);
        }
    }

    public async Task<SubtitleFile> DownloadAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct)
    {
        var url = $"{ApiUrl}subtitles/{Uri.EscapeDataString(candidate.ProviderId)}/download?api_key={Uri.EscapeDataString(Key)}";
        using var response = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), Name, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        ThrowIfError(response, download: true);

        return SubtitleArchive.IsZip(content)
            ? SubtitleArchive.Extract(content, query.Season, query.Kind == MediaKind.Series ? query.Episode : null, Name)
            : throw new SubtitleProviderException(Name, SubtitleProviderError.InvalidContent, "O SubSource não devolveu um arquivo ZIP.");
    }

    /// <summary>Testa a chave com uma busca mínima (para o botão "Testar").</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        using var _ = await GetJsonAsync("movies/search", [("searchType", "imdb"), ("imdb", "tt0944947"), ("season", "1")], refresh: true, ct)
            .ConfigureAwait(false);
        return "Chave válida.";
    }

    private string Key => settings().ApiKey?.Trim() ?? "";

    private async Task<JsonDocument> GetJsonAsync(string path, List<(string Name, string Value)> parameters, bool refresh, CancellationToken ct)
    {
        var queryString = string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        var cacheKey = $"{ProviderName}:{path}?{queryString}"; // sem a chave
        var json = refresh ? null : cache?.Get(cacheKey);

        if (json is null)
        {
            var url = $"{ApiUrl}{path}?api_key={Uri.EscapeDataString(Key)}&{queryString}";
            using var response = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), Name, ct).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return JsonDocument.Parse("""{"data":[]}""");
            ThrowIfError(response, download: false);
            cache?.Set(cacheKey, json);
        }

        return JsonDocument.Parse(json);
    }

    private void ThrowIfError(HttpResponseMessage response, bool download)
    {
        if (response.IsSuccessStatusCode) return;

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new SubtitleProviderException(Name, SubtitleProviderError.AuthenticationFailed, "A chave do SubSource foi recusada. Verifique nas Configurações."),
            HttpStatusCode.TooManyRequests when ResetAt(response) is { } reset && reset - DateTimeOffset.UtcNow > QuotaThreshold =>
                new SubtitleProviderException(Name, SubtitleProviderError.QuotaExceeded,
                    $"Limite do SubSource atingido; libera às {reset.ToLocalTime():HH:mm}.", reset),
            HttpStatusCode.TooManyRequests =>
                new SubtitleProviderException(Name, SubtitleProviderError.RateLimited, "Muitas requisições ao SubSource em pouco tempo. Tente de novo em instantes."),
            HttpStatusCode.NotFound when download =>
                new SubtitleProviderException(Name, SubtitleProviderError.InvalidContent, "A legenda não existe mais no SubSource."),
            _ => ProviderHttp.Unexpected(Name, response),
        };
    }

    /// <summary>Quando o limite libera: cabeçalho X-RateLimit-Reset (data ISO) ou Retry-After.</summary>
    internal static DateTimeOffset? ResetAt(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
            return reset;
        return ProviderHttp.RetryAfter(response) is { } wait ? DateTimeOffset.UtcNow + wait : null;
    }

    [GeneratedRegex(@"\bforced\b|foreign", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedRegex();

    [GeneratedRegex(@"\bsdh\b|\bhi\b|closed\s*caption|\bcc\b", RegexOptions.IgnoreCase)]
    private static partial Regex HearingImpairedRegex();

    [GeneratedRegex(@"hi\s*remove|non[\s-]?hi\b|non[\s-]?sdh|sdh\s*remove", RegexOptions.IgnoreCase)]
    private static partial Regex NonHearingImpairedRegex();

    // "Show.S01.1080p" (temporada sem episódio)
    [GeneratedRegex(@"\bS\d{1,2}(?!\s*E\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnlyRegex();
}
