namespace MediaOrganizer.Core.Parsing;

/// <summary>Informações extraídas do nome de arquivo de um episódio.</summary>
/// <param name="ShowName">Nome da série como aparece no arquivo (pode ser vazio).</param>
/// <param name="Year">Ano que acompanha o nome da série (ex.: "Dark Matter 2024"), usado só como dica de busca.</param>
/// <param name="Season">Temporada (1 quando o arquivo só tem "E01").</param>
/// <param name="Episode">Primeiro episódio.</param>
/// <param name="LastEpisode">Último episódio em arquivos com vários episódios ("S04E01-E02").</param>
/// <param name="EpisodeTitle">Título do episódio presente no arquivo, sem tags de release.</param>
public sealed record ParsedEpisode(
    string ShowName,
    int? Year,
    int Season,
    int Episode,
    int? LastEpisode,
    string? EpisodeTitle);

/// <summary>Informações extraídas do nome de arquivo/pasta de um filme.</summary>
public sealed record ParsedMovie(string Title, int? Year);
