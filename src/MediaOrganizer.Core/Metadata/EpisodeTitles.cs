namespace MediaOrganizer.Core.Metadata;

/// <summary>
/// Episódios de uma série no idioma definido pela <see cref="LanguagePolicy"/>, com fallback para inglês
/// quando o título é genérico. Cada temporada é buscada uma única vez por instância.
/// </summary>
/// <param name="refresh">Ignora o cache do TMDb (dados atualizados para séries em exibição).</param>
internal sealed class EpisodeTitles(TmdbClient tmdb, int showId, string language, bool refresh = false)
{
    /// <summary>Tamanho mínimo (normalizado) para casar um especial pelo título.</summary>
    private const int MinTitleMatchLength = 4;

    private readonly Dictionary<(int Season, string Language), IReadOnlyList<TvEpisode>> _seasons = [];

    /// <summary>Episódios da temporada (no idioma da obra), na ordem do TMDb. Vazio se a temporada não existir.</summary>
    public Task<IReadOnlyList<TvEpisode>> EpisodesAsync(int season, CancellationToken ct) => SeasonAsync(season, language, ct);

    public async Task<string?> GetAsync(int season, int episode, CancellationToken ct)
    {
        var title = Find(await SeasonAsync(season, language, ct).ConfigureAwait(false), episode);
        if (!LanguagePolicy.IsGenericEpisodeTitle(title) || language == LanguagePolicy.English) return Clean(title);

        var english = Find(await SeasonAsync(season, LanguagePolicy.English, ct).ConfigureAwait(false), episode);
        return Clean(LanguagePolicy.IsGenericEpisodeTitle(english) ? title : english);
    }

    /// <summary>
    /// Procura na temporada o episódio cujo título corresponde a <paramref name="text"/> (nome de arquivo sem número).
    /// Compara com os títulos no idioma da obra e em inglês: igualdade normalizada vence; senão, um título contido no outro.
    /// </summary>
    /// <returns>O número do episódio, ou <c>null</c> se nenhum título corresponder.</returns>
    public async Task<int?> FindByTitleAsync(int season, string text, CancellationToken ct)
    {
        var wanted = MediaMatcher.Normalize(text);
        if (wanted.Length < MinTitleMatchLength) return null;

        var candidates = new List<(int Episode, string Title)>();
        foreach (var lang in new[] { language, LanguagePolicy.English }.Distinct())
        {
            foreach (var episode in await SeasonAsync(season, lang, ct).ConfigureAwait(false))
            {
                if (LanguagePolicy.IsGenericEpisodeTitle(episode.Name)) continue;
                candidates.Add((episode.EpisodeNumber, MediaMatcher.Normalize(episode.Name!)));
            }
        }

        var exact = candidates.FirstOrDefault(c => c.Title == wanted);
        if (exact.Title is not null) return exact.Episode;

        // Contido: "pilot extended" contém "pilot"; prefere o título mais longo (mais específico).
        var partial = candidates
            .Where(c => c.Title.Length >= MinTitleMatchLength && (wanted.Contains(c.Title, StringComparison.Ordinal) || c.Title.Contains(wanted, StringComparison.Ordinal)))
            .OrderByDescending(c => c.Title.Length)
            .FirstOrDefault();
        return partial.Title is null ? null : partial.Episode;
    }

    private async Task<IReadOnlyList<TvEpisode>> SeasonAsync(int season, string lang, CancellationToken ct)
    {
        if (_seasons.TryGetValue((season, lang), out var cached)) return cached;

        var data = await tmdb.GetSeasonAsync(showId, season, lang, refresh, ct).ConfigureAwait(false);
        IReadOnlyList<TvEpisode> episodes = data?.Episodes ?? [];
        _seasons[(season, lang)] = episodes;
        return episodes;
    }

    private static string? Find(IReadOnlyList<TvEpisode> episodes, int number) =>
        episodes.FirstOrDefault(e => e.EpisodeNumber == number)?.Name;

    private static string? Clean(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();
}
