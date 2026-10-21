using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MediaOrganizer.Core.Metadata;

/// <summary>Falha de autenticação (API key ausente ou inválida).</summary>
public sealed class TmdbAuthenticationException(string message) : Exception(message);

/// <summary>
/// Cliente mínimo da API v3 do TMDb (https://developer.themoviedb.org/reference).
/// Aceita tanto a "API Key" (v3) quanto o "API Read Access Token" (v4, Bearer).
/// </summary>
public sealed class TmdbClient
{
    public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly MetadataCache? _cache;
    // Limite global de requisições simultâneas ao TMDb (compartilhado por todas as instâncias).
    private static readonly SemaphoreSlim Throttle = new(8);

    public TmdbClient(HttpClient http, Func<string?> apiKey, MetadataCache? cache = null)
    {
        _http = http;
        _http.BaseAddress ??= BaseAddress;
        _apiKey = apiKey;
        _cache = cache;
    }

    public async Task<IReadOnlyList<MediaCandidate>> SearchTvAsync(string query, int? year, string language, CancellationToken ct = default)
    {
        var page = await GetAsync("search/tv", [("query", query), ("first_air_date_year", year?.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("language", language)],
            TmdbJsonContext.Default.TmdbPageTvSearchResult, ct).ConfigureAwait(false);
        return page?.Results.Select(MediaCandidate.From).ToList() ?? [];
    }

    public async Task<IReadOnlyList<MediaCandidate>> SearchMovieAsync(string query, int? year, string language, CancellationToken ct = default)
    {
        var page = await GetAsync("search/movie", [("query", query), ("year", year?.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("language", language)],
            TmdbJsonContext.Default.TmdbPageMovieSearchResult, ct).ConfigureAwait(false);
        return page?.Results.Select(MediaCandidate.From).ToList() ?? [];
    }

    /// <param name="refresh">Ignora o cache e grava a resposta nova (séries em exibição).</param>
    public Task<TvDetails?> GetTvAsync(int id, string language, bool refresh = false, CancellationToken ct = default) =>
        GetAsync($"tv/{id}", [("language", language)], TmdbJsonContext.Default.TvDetails, ct, refresh);

    /// <param name="refresh">Ignora o cache e grava a resposta nova (séries em exibição).</param>
    public Task<TvSeason?> GetSeasonAsync(int id, int season, string language, bool refresh = false, CancellationToken ct = default) =>
        GetAsync($"tv/{id}/season/{season}", [("language", language)], TmdbJsonContext.Default.TvSeason, ct, refresh);

    public Task<MovieDetails?> GetMovieAsync(int id, string language, CancellationToken ct = default) =>
        GetAsync($"movie/{id}", [("language", language)], TmdbJsonContext.Default.MovieDetails, ct);

    /// <summary>Valida a chave fazendo uma chamada leve. Retorna <c>false</c> se a chave for recusada.</summary>
    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = BuildRequest("configuration", [], apiKey.Trim());
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> GetAsync<T>(string path, (string Name, string? Value)[] query, JsonTypeInfo<T> typeInfo, CancellationToken ct,
        bool refresh = false)
        where T : class
    {
        var cacheKey = path + "?" + BuildQuery(query);
        if (!refresh && _cache?.Get(cacheKey) is { } cached) return JsonSerializer.Deserialize(cached, typeInfo);

        var apiKey = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(apiKey))
            throw new TmdbAuthenticationException("Configure a API key do TMDb nas Configurações.");

        await Throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = BuildRequest(path, query, apiKey);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.Unauthorized:
                    throw new TmdbAuthenticationException("A API key do TMDb foi recusada. Verifique-a nas Configurações.");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _cache?.Set(cacheKey, json);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static HttpRequestMessage BuildRequest(string path, (string Name, string? Value)[] query, string apiKey)
    {
        // Token v4 (JWT) vai no cabeçalho; chave v3 vai na query string.
        var isBearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
        var queryString = BuildQuery(isBearer ? query : [.. query, ("api_key", apiKey)]);

        var request = new HttpRequestMessage(HttpMethod.Get, queryString.Length == 0 ? path : $"{path}?{queryString}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (isBearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static string BuildQuery((string Name, string? Value)[] query)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in query)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (builder.Length > 0) builder.Append('&');
            builder.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
        return builder.ToString();
    }
}
