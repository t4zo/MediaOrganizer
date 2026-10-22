using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles.Providers;

/// <summary>
/// OpenSubtitles.com (API REST v1, https://opensubtitles.stoplight.io). Busca por hash do arquivo + IMDb ID
/// (ou TMDb ID); o login é opcional e aumenta a cota diária de downloads.
/// </summary>
public sealed class OpenSubtitlesProvider(HttpClient http, Func<OpenSubtitlesSettings> settings, Func<bool> includeMachineTranslated,
    MetadataCache? cache = null) : ISubtitleProvider, IDisposable
{
    public const string ProviderName = "OpenSubtitles";
    public const string DefaultHost = "api.opensubtitles.com";

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private string? _tokenFor;
    private string _host = DefaultHost;

    public string Name => ProviderName;

    public bool IsConfigured => settings() is { Enabled: true, ApiKey.Length: > 0 };

    public int? RemainingDownloads { get; private set; }

    public void Dispose() => _loginLock.Dispose();

    private Uri Url(string path) => new($"https://{_host}/api/v1/{path}");

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        var parameters = SearchParameters(query, includeMachineTranslated());
        var queryString = string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        var cacheKey = $"{ProviderName}:subtitles?{queryString}";

        var json = query.Refresh ? null : cache?.Get(cacheKey);
        if (json is null)
        {
            using var response = await ProviderHttp.SendAsync(http, () => Request(HttpMethod.Get, Url($"subtitles?{queryString}")), Name, ct)
                .ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ThrowIfError(response, json);
            cache?.Set(cacheKey, json);
        }

        return Parse(json, query, searchedById: query.ImdbId is not null || query.TmdbId > 0);
    }

    /// <summary>Parâmetros em ordem alfabética e em minúsculas, como a API pede (senão ela redireciona).</summary>
    internal static List<(string Name, string Value)> SearchParameters(SubtitleQuery query, bool includeMachineTranslated)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("languages", string.Join(',', query.Languages.Select(l => LanguageCode(l.Code)).Distinct().Order(StringComparer.Ordinal))),
        };
        if (!includeMachineTranslated) parameters.Add(("ai_translated", "exclude"));
        else parameters.Add(("machine_translated", "include"));
        if (query.FileHash is not null) parameters.Add(("moviehash", query.FileHash));

        var imdb = NumericImdbId(query.ImdbId);
        if (query.Kind == MediaKind.Series)
        {
            if (imdb is not null) parameters.Add(("parent_imdb_id", imdb));
            else parameters.Add(("parent_tmdb_id", query.TmdbId.ToString(CultureInfo.InvariantCulture)));
            if (query.Season is { } season) parameters.Add(("season_number", season.ToString(CultureInfo.InvariantCulture)));
            if (query.Episode is { } episode) parameters.Add(("episode_number", episode.ToString(CultureInfo.InvariantCulture)));
            parameters.Add(("type", "episode"));
        }
        else
        {
            if (imdb is not null) parameters.Add(("imdb_id", imdb));
            else parameters.Add(("tmdb_id", query.TmdbId.ToString(CultureInfo.InvariantCulture)));
            parameters.Add(("type", "movie"));
        }

        return [.. parameters.Select(p => (p.Name, p.Value.ToLowerInvariant())).OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>"pt-BR" → "pt-br"; demais em minúsculas.</summary>
    internal static string LanguageCode(string code) => code.ToLowerInvariant();

    /// <summary>"tt0944947" → "944947" (a API usa o número sem "tt" e sem zeros à esquerda).</summary>
    internal static string? NumericImdbId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return null;
        var digits = imdbId.Trim().TrimStart('t', 'T').TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private List<SubtitleCandidate> Parse(string json, SubtitleQuery query, bool searchedById)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<SubtitleCandidate>();

        foreach (var item in document.RootElement.Items("data"))
        {
            if (item.Prop("attributes") is not { } attributes) continue;
            if (attributes.Items("files").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } file || file.Long("file_id") is not { } fileId) continue;

            var hearingImpaired = attributes.Bool("hearing_impaired");
            var forced = attributes.Bool("foreign_parts_only") && !hearingImpaired;
            var code = SubtitleLanguages.Normalize(attributes.Str("language") ?? "");
            var language = new SubtitleLanguage(code, forced, hearingImpaired);
            if (!query.Languages.Any(l => l.SameKind(language))) continue;

            var machine = attributes.Bool("ai_translated") || attributes.Bool("machine_translated");
            var hashMatch = attributes.Bool("moviehash_match");
            var details = attributes.Prop("feature_details");
            var episodeMatches = query.Kind != MediaKind.Series ||
                                 (details?.Int("season_number") == query.Season && details?.Int("episode_number") == query.Episode);
            if (!episodeMatches && !hashMatch) continue;

            var fileName = file.Str("file_name");
            var format = Path.GetExtension(fileName ?? "") is { Length: > 1 } ext && MediaFileTypes.IsSubtitle(fileName!) ? ext[1..].ToLowerInvariant() : "srt";
            result.Add(new SubtitleCandidate(
                Name,
                fileId.ToString(CultureInfo.InvariantCulture),
                language,
                attributes.Str("release") ?? fileName,
                hashMatch,
                IdMatch: searchedById && episodeMatches,
                IsSeasonPack: false,
                attributes.Int("download_count") ?? 0,
                format,
                machine));
        }

        return result;
    }

    public async Task<SubtitleFile> DownloadAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct)
    {
        var token = await EnsureLoginAsync(ct).ConfigureAwait(false);
        var body = JsonElementExtensions.Object(("file_id", long.Parse(candidate.ProviderId, CultureInfo.InvariantCulture)));

        using var response = await ProviderHttp.SendAsync(http, () =>
        {
            var request = Request(HttpMethod.Post, Url("download"), token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, Name, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotAcceptable) throw QuotaExceeded(json);
        ThrowIfError(response, json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RemainingDownloads = root.Int("remaining") ?? RemainingDownloads;
        if (root.Str("link") is not { Length: > 0 } link)
            throw new SubtitleProviderException(Name, SubtitleProviderError.InvalidContent, "O OpenSubtitles não devolveu o link de download.");

        using var file = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, link), Name, ct).ConfigureAwait(false);
        if (!file.IsSuccessStatusCode) throw ProviderHttp.Unexpected(Name, file);
        var content = await file.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new SubtitleFile(content, candidate.Format);
    }

    /// <summary>Faz login (uma vez por sessão e por usuário) se houver usuário e senha. Sem login, o download usa só a chave.</summary>
    private async Task<string?> EnsureLoginAsync(CancellationToken ct)
    {
        var current = settings();
        if (string.IsNullOrWhiteSpace(current.Username) || string.IsNullOrEmpty(current.Password)) return null;

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var identity = current.Username + "\n" + current.ApiKey;
            if (_token is not null && _tokenFor == identity) return _token;

            var body = JsonElementExtensions.Object(("username", current.Username.Trim()), ("password", current.Password));
            _host = DefaultHost;
            using var response = await ProviderHttp.SendAsync(http, () =>
            {
                var request = Request(HttpMethod.Post, Url("login"));
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return request;
            }, Name, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SubtitleProviderException(Name, SubtitleProviderError.AuthenticationFailed,
                    "Usuário ou senha do OpenSubtitles recusados. Verifique nas Configurações.");
            var loginJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ThrowIfError(response, loginJson);

            using var document = JsonDocument.Parse(loginJson);
            var root = document.RootElement;
            _token = root.Str("token");
            _tokenFor = identity;
            if (root.Str("base_url") is { Length: > 0 } baseUrl) _host = new Uri(baseUrl.Contains("://", StringComparison.Ordinal) ? baseUrl : "https://" + baseUrl).Host;
            if (root.Prop("user") is { } user) RemainingDownloads = user.Int("remaining_downloads") ?? user.Int("allowed_downloads") ?? RemainingDownloads;
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Testa a chave (e o login, se informado) para o botão "Testar" das Configurações.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        _token = null;
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        using var response = await ProviderHttp.SendAsync(http, () => Request(HttpMethod.Get, Url("infos/formats")), Name, ct).ConfigureAwait(false);
        ThrowIfError(response);
        return _token is null
            ? "Chave válida (sem login: cota pequena de downloads por dia)."
            : $"Chave e login válidos{(RemainingDownloads is { } r ? $"; {r} download(s) restante(s) hoje" : "")}.";
    }

    private HttpRequestMessage Request(HttpMethod method, Uri url, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Api-Key", settings().ApiKey?.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <param name="body">Resposta já lida: em erros inesperados, a mensagem da API ("message"/"errors") vai junto.</param>
    private void ThrowIfError(HttpResponseMessage response, string? body = null)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new SubtitleProviderException(Name, SubtitleProviderError.AuthenticationFailed,
                    "A chave do OpenSubtitles foi recusada. Verifique nas Configurações.");
            case HttpStatusCode.TooManyRequests:
                throw new SubtitleProviderException(Name, SubtitleProviderError.RateLimited,
                    "Muitas requisições ao OpenSubtitles em pouco tempo. Tente de novo em instantes.");
            case HttpStatusCode.NotAcceptable:
                throw QuotaExceeded(body);
        }
        if (response.IsSuccessStatusCode) return;
        if (ApiMessage(body) is not { } message) throw ProviderHttp.Unexpected(Name, response);
        throw new SubtitleProviderException(Name, SubtitleProviderError.Unavailable,
            $"{Name} respondeu HTTP {(int)response.StatusCode}: {message}");
    }

    /// <summary>"message" ou o primeiro de "errors" de uma resposta de erro, se for JSON.</summary>
    internal static string? ApiMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var message = root.Str("message") ??
                          root.Items("errors").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).FirstOrDefault(e => e is { Length: > 0 });
            return message is { Length: > 0 } ? message.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SubtitleProviderException QuotaExceeded(string? json)
    {
        DateTimeOffset? reset = null;
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                RemainingDownloads = document.RootElement.Int("remaining") ?? 0;
                if (DateTimeOffset.TryParse(document.RootElement.Str("reset_time_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    reset = parsed;
            }
            catch (JsonException)
            {
            }
        }

        RemainingDownloads = 0;
        var when = reset is { } r ? $"; renova às {r.ToLocalTime():HH:mm}" : "";
        return new SubtitleProviderException(Name, SubtitleProviderError.QuotaExceeded, $"Cota diária do OpenSubtitles esgotada{when}.", reset);
    }
}
