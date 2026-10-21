using MediaOrganizer.Core.Parsing;

namespace MediaOrganizer.Core.Planning;

/// <summary>Legenda externa associada a um vídeo. <paramref name="Flag"/>: "forced", "sdh" ou <c>null</c>.</summary>
public sealed record ExternalSubtitle(string Path, string? Language, string? Flag);

/// <summary>
/// Associa legendas aos vídeos e calcula o novo nome "&lt;vídeo&gt;.&lt;idioma&gt;[.forced|.sdh].ext".
/// As legendas são renomeadas na pasta onde já estão.
/// </summary>
public static partial class SubtitleMatcher
{
    private static readonly FrozenDictionary<string, string> LanguageCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en", ["eng"] = "en", ["english"] = "en", ["inglês"] = "en", ["ingles"] = "en",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt", ["português"] = "pt", ["portugues"] = "pt",
        ["pob"] = "pt-BR", ["ptbr"] = "pt-BR", ["brazilian"] = "pt-BR", ["brazil"] = "pt-BR",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es", ["español"] = "es", ["espanol"] = "es", ["espanhol"] = "es",
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr", ["français"] = "fr",
        ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["german"] = "de", ["deutsch"] = "de",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it", ["italiano"] = "it",
        ["nl"] = "nl", ["dut"] = "nl", ["nld"] = "nl", ["dutch"] = "nl",
        ["sv"] = "sv", ["swe"] = "sv", ["swedish"] = "sv",
        ["da"] = "da", ["dan"] = "da", ["danish"] = "da",
        ["no"] = "no", ["nor"] = "no", ["norwegian"] = "no",
        ["fi"] = "fi", ["fin"] = "fi", ["finnish"] = "fi",
        ["pl"] = "pl", ["pol"] = "pl", ["polish"] = "pl",
        ["cs"] = "cs", ["cze"] = "cs", ["ces"] = "cs", ["czech"] = "cs",
        ["hu"] = "hu", ["hun"] = "hu", ["hungarian"] = "hu",
        ["ro"] = "ro", ["rum"] = "ro", ["ron"] = "ro", ["romanian"] = "ro",
        ["tr"] = "tr", ["tur"] = "tr", ["turkish"] = "tr",
        ["el"] = "el", ["gre"] = "el", ["ell"] = "el", ["greek"] = "el",
        ["ru"] = "ru", ["rus"] = "ru", ["russian"] = "ru",
        ["uk"] = "uk", ["ukr"] = "uk", ["ukrainian"] = "uk",
        ["ar"] = "ar", ["ara"] = "ar", ["arabic"] = "ar",
        ["he"] = "he", ["heb"] = "he", ["hebrew"] = "he",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko",
        ["zh"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["chinese"] = "zh",
        ["hi"] = "hi", ["hin"] = "hi", ["hindi"] = "hi",
        ["th"] = "th", ["tha"] = "th", ["thai"] = "th",
        ["vi"] = "vi", ["vie"] = "vi", ["vietnamese"] = "vi",
        ["id"] = "id", ["ind"] = "id", ["indonesian"] = "id",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <param name="videoTargets">Vídeo original → novo nome base (sem extensão) do vídeo.</param>
    /// <param name="subtitles">Legendas encontradas na pasta.</param>
    public static IReadOnlyList<RenameItem> Plan(IReadOnlyDictionary<string, string> videoTargets, IEnumerable<string> subtitles)
    {
        var items = new List<RenameItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subtitle in subtitles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var match = FindVideo(subtitle, videoTargets.Keys);
            if (match is null)
            {
                items.Add(new RenameItem(ItemKind.Subtitle, subtitle, subtitle, ItemStatus.Skipped, "Vídeo correspondente não encontrado."));
                continue;
            }

            var (video, tokens) = match.Value;
            var (language, flag) = DetectLanguage(tokens);
            var directory = Path.GetDirectoryName(subtitle)!;
            var extension = Path.GetExtension(subtitle);
            var stem = videoTargets[video] + (language is null ? "" : "." + language) + (flag is null ? "" : "." + flag);

            var target = Path.Combine(directory, stem + extension);
            for (var n = 2; used.Contains(target); n++) target = Path.Combine(directory, $"{stem}.{n}{extension}");
            used.Add(target);

            var status = RenameItem.PathsEqual(subtitle, target, ignoreCase: false) ? ItemStatus.Unchanged : ItemStatus.Ok;
            items.Add(new RenameItem(ItemKind.Subtitle, subtitle, target, status));
        }

        return items;
    }

    /// <summary>Legendas externas de cada vídeo, com idioma e flag detectados (mesmas regras da renomeação).</summary>
    public static IReadOnlyDictionary<string, List<ExternalSubtitle>> Assign(IReadOnlyCollection<string> videos, IEnumerable<string> subtitles)
    {
        var result = videos.ToDictionary(v => v, _ => new List<ExternalSubtitle>(), StringComparer.OrdinalIgnoreCase);
        foreach (var subtitle in subtitles)
        {
            if (FindVideo(subtitle, videos) is not var (video, tokens)) continue;
            var (language, flag) = DetectLanguage(tokens);
            result[video].Add(new ExternalSubtitle(subtitle, language, flag));
        }
        return result;
    }

    /// <returns>O vídeo dono da legenda e o trecho do nome que descreve idioma/flags.</returns>
    private static (string Video, string Tokens)? FindVideo(string subtitle, IEnumerable<string> videos)
    {
        var videoList = videos as IReadOnlyCollection<string> ?? [.. videos];
        var directory = Path.GetDirectoryName(subtitle)!;
        var name = Path.GetFileNameWithoutExtension(subtitle);

        // 1) Mesmo diretório, nome começando pelo nome do vídeo: "Ep.en.srt", "Ep.forced.en.srt".
        var sameDir = videoList
            .Where(v => PathEquals(Path.GetDirectoryName(v), directory))
            .Select(v => (Video: v, Base: Path.GetFileNameWithoutExtension(v)))
            .Where(v => name.StartsWith(v.Base, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Base.Length)
            .FirstOrDefault();
        if (sameDir.Video is not null) return (sameDir.Video, name[sameDir.Base.Length..]);

        // 2) Padrão RARBG: <pasta do vídeo>/Subs/<nome do vídeo>/3_English.srt
        var parent = Directory.GetParent(directory);
        if (parent?.Parent is { } grandParent && parent.Name.Equals("Subs", StringComparison.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(directory);
            var owner = videoList.FirstOrDefault(v =>
                PathEquals(Path.GetDirectoryName(v), grandParent.FullName) &&
                Path.GetFileNameWithoutExtension(v).Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (owner is not null) return (owner, name);
        }

        // 3) Pasta Subs/Subtitles com um único vídeo na pasta acima (comum em filmes).
        if (parent is not null && SubsFolderRegex().IsMatch(Path.GetFileName(directory)))
        {
            var candidates = videoList.Where(v => PathEquals(Path.GetDirectoryName(v), parent.FullName)).ToList();
            if (candidates.Count == 1) return (candidates[0], name);
        }

        // 4) Mesma temporada/episódio na mesma pasta ou na pasta acima.
        if (ReleaseNameParser.ParseEpisode(name) is { } episode)
        {
            var owner = videoList.FirstOrDefault(v =>
                (PathEquals(Path.GetDirectoryName(v), directory) || PathEquals(Path.GetDirectoryName(v), parent?.FullName)) &&
                ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(v)) is { } ve &&
                ve.Season == episode.Season && ve.Episode == episode.Episode);
            if (owner is not null) return (owner, LastTokens(name));
        }

        return null;
    }

    /// <summary>Código ISO 639-1 de um nome/código de idioma ("English", "eng" → "en"), ou <c>null</c>.</summary>
    internal static string? LanguageCodeFor(string token) => LanguageCodes.TryGetValue(token, out var code) ? code : null;

    /// <summary>"Portuguese (Brazil)", "PT-BR", "pob", "Brasil"...</summary>
    internal static bool MentionsBrazil(string text) => PortugueseBrazilRegex().IsMatch(text) || text.Contains("brasil", StringComparison.OrdinalIgnoreCase);

    internal static (string? Language, string? Flag) DetectLanguage(string tokensText)
    {
        string? language = PortugueseBrazilRegex().IsMatch(tokensText) ? "pt-BR" : null;
        string? flag = null;

        foreach (var token in TokenSplitRegex().Split(tokensText))
        {
            if (token.Length == 0) continue;
            if (token.Equals("forced", StringComparison.OrdinalIgnoreCase)) flag = "forced";
            else if (token is "sdh" or "SDH" or "cc" or "CC" or "hi" or "HI") flag ??= "sdh";
            else if (language is null && LanguageCodes.TryGetValue(token, out var code)) language = code;
        }

        return (language, flag);
    }

    /// <summary>Considera só o fim do nome para não confundir palavras do título com idiomas.</summary>
    private static string LastTokens(string name)
    {
        var parts = name.Split(['.', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.TakeLast(3));
    }

    private static bool PathEquals(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\s._\-()\[\]]+")]
    private static partial Regex TokenSplitRegex();

    [GeneratedRegex(@"pt[\s._\-]?br|\bpob\b|brazil", RegexOptions.IgnoreCase)]
    private static partial Regex PortugueseBrazilRegex();

    [GeneratedRegex(@"^(subs?|subtitles?|legendas?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubsFolderRegex();
}
