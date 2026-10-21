namespace MediaOrganizer.Core.Parsing;

/// <summary>
/// Interpreta nomes de arquivo no padrão de download
/// (ex.: <c>American.Primeval.S01E01.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA</c>).
/// Recebe o nome <b>sem extensão</b>. As regras estão documentadas em docs/SPEC.md.
/// </summary>
public static partial class ReleaseNameParser
{
    /// <summary>Tags técnicas que marcam o fim do nome útil em um release.</summary>
    internal const string TechnicalTags =
        @"2160p|1440p|1080p|1080i|720p|576p|480p|4k|uhd|10bit|8bit|hdr10\+?|hdr|dv|dovi|sdr|" +
        @"web-?rip|web-?dl|blu-?ray|bdrip|brrip|bdremux|remux|hdtv|dvdrip|dvd|hdrip|" +
        @"x264|x265|h ?264|h ?265|hevc|avc|xvid|av1|" +
        @"aac\d?|ac3|eac3|ddp?\d?|dd\+|dts(?:-hd)?|truehd|atmos|flac|opus|mp3|\dch|" +
        @"repack\d?|proper|internal|dublado|legendado|" +
        @"amzn|nf|dsnp|hmax|atvp|pcok|pmtp";

    /// <summary>Palavras de edição: são tags em episódios/filmes, mas fazem parte do título de especiais ("Pilot Extended").</summary>
    internal const string EditionTags = "extended|unrated|remastered|imax";

    internal const string ReleaseTags = TechnicalTags + "|" + EditionTags;

    /// <summary>Converte separadores de release em espaços. Pontos entre dígitos ("1.23.45") são preservados.</summary>
    public static string NormalizeSeparators(string name)
    {
        var text = DotSeparatorRegex().Replace(name, " ").Replace('_', ' ');
        return MultiSpaceRegex().Replace(text, " ").Trim();
    }

    /// <summary>Extrai temporada/episódio. Retorna <c>null</c> quando o nome não tem marcador de episódio.</summary>
    public static ParsedEpisode? ParseEpisode(string fileNameWithoutExtension)
    {
        var text = NormalizeSeparators(fileNameWithoutExtension);

        var match = SeasonEpisodeRegex().Match(text);
        if (!match.Success) match = CrossFormatRegex().Match(text);
        if (!match.Success) match = EpisodeOnlyRegex().Match(text);
        if (!match.Success) return null;

        var season = match.Groups["s"].Success ? ToInt(match.Groups["s"].ValueSpan) : 1;
        var episode = ToInt(match.Groups["e"].ValueSpan);
        int? lastEpisode = match.Groups["e2"].Success ? ToInt(match.Groups["e2"].ValueSpan) : null;
        if (lastEpisode <= episode) lastEpisode = null;

        var (show, year) = SplitYear(TrimSeparators(text[..match.Index]));
        var title = CleanTitle(text[(match.Index + match.Length)..]);

        return new ParsedEpisode(show, year, season, episode, lastEpisode, title);
    }

    /// <summary>Extrai título e ano de um filme ("Inception.2010.1080p.BluRay" → "Inception", 2010).</summary>
    public static ParsedMovie ParseMovie(string nameWithoutExtension)
    {
        var text = CutAtReleaseTags(NormalizeSeparators(nameWithoutExtension));

        // O ano é o último ano plausível que não esteja no início ("2001 A Space Odyssey 1968").
        var years = YearRegex().Matches(text).Where(m => m.Index > 0).ToList();
        if (years.Count == 0) return new ParsedMovie(TrimSeparators(text), null);

        var yearMatch = years[^1];
        var title = TrimSeparators(text[..yearMatch.Index]);
        return new ParsedMovie(title, ToInt(yearMatch.Groups["y"].ValueSpan));
    }

    /// <summary>
    /// Texto útil de um especial sem número ("Breaking.Bad.-.Pilot.Extended.720p" → "Pilot Extended"):
    /// normaliza, corta nas tags técnicas e remove o nome da série do início (qualquer um dos informados).
    /// </summary>
    /// <returns>O texto, ou <c>null</c> se não sobrar nada.</returns>
    public static string? ParseSpecialTitle(string nameWithoutExtension, IEnumerable<string?> showNames)
    {
        var text = NormalizeSeparators(nameWithoutExtension);
        var tag = TechnicalTagRegex().Match(text);
        text = TrimSeparators(tag.Success ? text[..tag.Index] : text);

        foreach (var show in showNames.OfType<string>().Select(NormalizeSeparators).Where(s => s.Length > 0).OrderByDescending(s => s.Length))
        {
            if (text.StartsWith(show, StringComparison.OrdinalIgnoreCase))
            {
                text = TrimSeparators(text[show.Length..]);
                break;
            }
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>Remove tudo a partir da primeira tag técnica (o "-GRUPO" do release vem depois dela e cai junto).</summary>
    internal static string CutAtReleaseTags(string text)
    {
        var tag = ReleaseTagRegex().Match(text);
        if (tag.Success) text = text[..tag.Index];
        return TrimSeparators(text);
    }

    private static string? CleanTitle(string rest)
    {
        var title = CutAtReleaseTags(rest);
        return title.Length == 0 ? null : title;
    }

    private static (string Show, int? Year) SplitYear(string show)
    {
        var match = TrailingYearRegex().Match(show);
        if (!match.Success || match.Index == 0) return (show, null);
        return (TrimSeparators(show[..match.Index]), ToInt(match.Groups["y"].ValueSpan));
    }

    private static int ToInt(ReadOnlySpan<char> digits) => int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

    private static string TrimSeparators(string text) => text.Trim(' ', '-', '–', '_', '.', '[', '(', ',');

    // "S01E01", "S04E01-E02", "S01E01E02", "S01 E01"
    [GeneratedRegex(@"\bS(?<s>\d{1,2})\s?E(?<e>\d{1,3})(?:\s?-?\s?E(?<e2>\d{1,3}))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    // "1x01"
    [GeneratedRegex(@"\b(?<s>\d{1,2})x(?<e>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CrossFormatRegex();

    // "E01" ou "Ep 01" sem temporada (minisséries como Band of Brothers)
    [GeneratedRegex(@"\b(?:E|Ep)\s?(?<e>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyRegex();

    [GeneratedRegex(@"[\(\[]?\b(?:" + ReleaseTags + @")\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTagRegex();

    [GeneratedRegex(@"[\(\[]?\b(?:" + TechnicalTags + @")\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTagRegex();

    [GeneratedRegex(@"[\(\[]?(?<y>(?:19|20)\d{2})[\)\]]?$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"[\(\[]?\b(?<y>(?:19|20)\d{2})\b[\)\]]?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)")]
    private static partial Regex DotSeparatorRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
