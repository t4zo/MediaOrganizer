namespace MediaOrganizer.Core.Naming;

/// <summary>Monta os nomes finais no padrão do MediaOrganizer.</summary>
public static class NameFormatter
{
    /// <summary>"Série S01E01 - Título" ou "Série S04E01-E02 - Título A &amp; Título B" (sem extensão).</summary>
    public static string EpisodeBaseName(string show, int season, int episode, int? lastEpisode, string? title)
    {
        var code = lastEpisode is { } last
            ? $"S{season:00}E{episode:00}-E{last:00}"
            : $"S{season:00}E{episode:00}";

        var baseName = $"{FileNameSanitizer.Sanitize(show)} {code}";
        return string.IsNullOrWhiteSpace(title) ? baseName : $"{baseName} - {FileNameSanitizer.Sanitize(title)}";
    }

    /// <summary>"Filme (Ano)" (sem extensão).</summary>
    public static string MovieBaseName(string title, int? year)
    {
        var clean = FileNameSanitizer.Sanitize(title);
        return year is { } y ? $"{clean} ({y})" : clean;
    }
}
