namespace MediaOrganizer.Core.Metadata;

public sealed record TmdbPage<T>(IReadOnlyList<T> Results);

public sealed record TvSearchResult(
    int Id,
    string Name,
    string OriginalName,
    string? OriginalLanguage,
    string? FirstAirDate,
    string? Overview,
    string? PosterPath,
    double Popularity);

public sealed record TvDetails(
    int Id,
    string Name,
    string OriginalName,
    string? OriginalLanguage,
    string? FirstAirDate,
    int NumberOfSeasons,
    string? Overview,
    string? PosterPath,
    IReadOnlyList<TvSeasonSummary>? Seasons = null);

/// <summary>Resumo de uma temporada em <c>tv/{id}</c> (a temporada 0 são os especiais).</summary>
public sealed record TvSeasonSummary(int SeasonNumber, int EpisodeCount, string? AirDate, string? Name);

public sealed record TvSeason(int SeasonNumber, IReadOnlyList<TvEpisode> Episodes);

public sealed record TvEpisode(int EpisodeNumber, string? Name, string? AirDate)
{
    /// <summary>Data de exibição ("2008-01-20"), ou <c>null</c> se ausente/inválida.</summary>
    public DateOnly? AiredOn => ParseDate(AirDate);

    public static DateOnly? ParseDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var value) ? value : null;
}

public sealed record MovieSearchResult(
    int Id,
    string Title,
    string OriginalTitle,
    string? OriginalLanguage,
    string? ReleaseDate,
    string? Overview,
    string? PosterPath,
    double Popularity);

public sealed record MovieDetails(
    int Id,
    string Title,
    string OriginalTitle,
    string? OriginalLanguage,
    string? ReleaseDate,
    string? Overview,
    string? PosterPath,
    string? ImdbId = null);

/// <summary>IDs em outras bases (<c>tv/{id}/external_ids</c>); o IMDb é o que os sites de legenda aceitam melhor.</summary>
public sealed record ExternalIds(string? ImdbId, int? TvdbId);

/// <summary>Resultado de busca unificado (série ou filme) exibido na tela de correspondência.</summary>
public sealed record MediaCandidate(
    MediaKind Kind,
    int Id,
    string Title,
    string OriginalTitle,
    string? OriginalLanguage,
    int? Year,
    string? Overview,
    string? PosterPath,
    double Popularity)
{
    public static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) ? year : null;

    public static MediaCandidate From(TvSearchResult r) =>
        new(MediaKind.Series, r.Id, r.Name, r.OriginalName, r.OriginalLanguage, YearOf(r.FirstAirDate), r.Overview, r.PosterPath, r.Popularity);

    public static MediaCandidate From(TvDetails r) =>
        new(MediaKind.Series, r.Id, r.Name, r.OriginalName, r.OriginalLanguage, YearOf(r.FirstAirDate), r.Overview, r.PosterPath, 0);

    public static MediaCandidate From(MovieSearchResult r) =>
        new(MediaKind.Movie, r.Id, r.Title, r.OriginalTitle, r.OriginalLanguage, YearOf(r.ReleaseDate), r.Overview, r.PosterPath, r.Popularity);

    public static MediaCandidate From(MovieDetails r) =>
        new(MediaKind.Movie, r.Id, r.Title, r.OriginalTitle, r.OriginalLanguage, YearOf(r.ReleaseDate), r.Overview, r.PosterPath, 0);

    /// <summary>Título final conforme a <see cref="LanguagePolicy"/>: original se latino, senão inglês.</summary>
    public string PreferredTitle => LanguagePolicy.UsesOriginal(OriginalLanguage) ? OriginalTitle : Title;

    public string? PosterUrl => PosterPath is null ? null : $"https://image.tmdb.org/t/p/w154{PosterPath}";

    public string WebUrl => Kind == MediaKind.Series
        ? $"https://www.themoviedb.org/tv/{Id}"
        : $"https://www.themoviedb.org/movie/{Id}";

    public string DisplayName => Year is { } y ? $"{PreferredTitle} ({y})" : PreferredTitle;
}
