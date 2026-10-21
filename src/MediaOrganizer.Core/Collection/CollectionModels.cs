using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Collection;

public enum CollectionStatus
{
    /// <summary>Todos os episódios já exibidos estão na pasta.</summary>
    Complete,
    /// <summary>Faltam episódios já exibidos.</summary>
    Incomplete,
    /// <summary>Pasta sem correspondência com o TMDb.</summary>
    NoMatch,
    /// <summary>Falha ao consultar o TMDb.</summary>
    Error,
}

/// <summary>Um episódio oficial (título conforme a <see cref="LanguagePolicy"/>).</summary>
public sealed record EpisodeRef(int Season, int Episode, string? Title, DateOnly? AirDate)
{
    public string Code => $"S{Season:00}E{Episode:00}";
}

/// <summary>Situação de uma temporada: só episódios já exibidos contam como esperados.</summary>
/// <param name="Aired">Episódios já exibidos (data de exibição até hoje).</param>
/// <param name="Present">Episódios exibidos que estão na pasta.</param>
/// <param name="Upcoming">Episódios ainda não exibidos ou sem data.</param>
public sealed record SeasonReport(int Season, int Aired, int Present, IReadOnlyList<EpisodeRef> Missing, IReadOnlyList<EpisodeRef> Upcoming)
{
    public bool IsSpecials => Season == 0;
    public bool IsComplete => Missing.Count == 0;
    public bool IsNotStarted => Aired > 0 && Present == 0;
}

/// <summary>Resultado da Coleção para uma série.</summary>
public sealed record SeriesReport(
    LibraryFolder Folder,
    MediaCandidate? Show,
    IReadOnlyList<SeasonReport> Seasons,
    CollectionStatus Status,
    string? Message = null)
{
    /// <summary>Temporadas regulares (sem os especiais), que definem a situação e o progresso.</summary>
    public IEnumerable<SeasonReport> RegularSeasons => Seasons.Where(s => !s.IsSpecials);

    public SeasonReport? Specials => Seasons.FirstOrDefault(s => s.IsSpecials);

    public int Aired => RegularSeasons.Sum(s => s.Aired);
    public int Present => RegularSeasons.Sum(s => s.Present);
    public int MissingCount => RegularSeasons.Sum(s => s.Missing.Count);
}
