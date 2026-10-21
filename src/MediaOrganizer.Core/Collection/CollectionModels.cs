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

public enum EpisodeState
{
    /// <summary>Exibido e na pasta.</summary>
    Present,
    /// <summary>Exibido e ausente da pasta.</summary>
    Missing,
    /// <summary>Ainda não exibido (data futura ou sem data) e ausente da pasta.</summary>
    Upcoming,
    /// <summary>Na pasta, mas o TMDb ainda não registra a exibição.</summary>
    PresentNotAired,
}

/// <summary>Um episódio oficial e sua situação na biblioteca.</summary>
public sealed record EpisodeStatus(EpisodeRef Episode, EpisodeState State);

/// <summary>
/// Situação de uma temporada, com todos os episódios oficiais na ordem do TMDb.
/// Só episódios já exibidos contam como esperados.
/// </summary>
public sealed record SeasonReport(int Season, IReadOnlyList<EpisodeStatus> Episodes)
{
    /// <summary>Episódios já exibidos (data de exibição até hoje).</summary>
    public int Aired => Episodes.Count(e => e.State is EpisodeState.Present or EpisodeState.Missing);

    /// <summary>Episódios exibidos que estão na pasta.</summary>
    public int Present => Episodes.Count(e => e.State == EpisodeState.Present);

    public IReadOnlyList<EpisodeRef> Missing => [.. Episodes.Where(e => e.State == EpisodeState.Missing).Select(e => e.Episode)];

    /// <summary>Episódios ainda não exibidos ou sem data, ausentes da pasta.</summary>
    public IReadOnlyList<EpisodeRef> Upcoming => [.. Episodes.Where(e => e.State == EpisodeState.Upcoming).Select(e => e.Episode)];

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
