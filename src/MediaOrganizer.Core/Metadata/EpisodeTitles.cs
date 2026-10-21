namespace MediaOrganizer.Core.Metadata;

/// <summary>
/// Títulos dos episódios de uma série no idioma definido pela <see cref="LanguagePolicy"/>,
/// com fallback para inglês quando o título é genérico. Cada temporada é buscada uma única vez.
/// </summary>
internal sealed class EpisodeTitles(TmdbClient tmdb, int showId, string language)
{
    /// <summary>Tamanho mínimo (normalizado) para casar um especial pelo título.</summary>
    private const int MinTitleMatchLength = 4;

    private readonly Dictionary<(int Season, string Language), IReadOnlyDictionary<int, string?>> _seasons = [];

    public async Task<string?> GetAsync(int season, int episode, CancellationToken ct)
    {
        var title = (await SeasonAsync(season, language, ct).ConfigureAwait(false)).GetValueOrDefault(episode);
        if (!LanguagePolicy.IsGenericEpisodeTitle(title) || language == LanguagePolicy.English) return Clean(title);

        var english = (await SeasonAsync(season, LanguagePolicy.English, ct).ConfigureAwait(false)).GetValueOrDefault(episode);
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
            foreach (var (episode, title) in await SeasonAsync(season, lang, ct).ConfigureAwait(false))
            {
                if (LanguagePolicy.IsGenericEpisodeTitle(title)) continue;
                candidates.Add((episode, MediaMatcher.Normalize(title!)));
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

    private async Task<IReadOnlyDictionary<int, string?>> SeasonAsync(int season, string lang, CancellationToken ct)
    {
        if (_seasons.TryGetValue((season, lang), out var cached)) return cached;

        var data = await tmdb.GetSeasonAsync(showId, season, lang, ct).ConfigureAwait(false);
        var map = data?.Episodes
            .GroupBy(e => e.EpisodeNumber)
            .ToDictionary(g => g.Key, g => g.First().Name) ?? [];
        _seasons[(season, lang)] = map;
        return map;
    }

    private static string? Clean(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();
}
