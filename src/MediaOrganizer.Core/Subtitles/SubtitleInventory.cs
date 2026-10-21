using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma legenda que o vídeo já tem: arquivo externo (<see cref="Path"/>) ou faixa embutida no MKV.</summary>
public sealed record ExistingSubtitle(SubtitleLanguage Language, string? Path, bool Embedded);

/// <summary>Legendas de um vídeo e o que falta em relação aos idiomas desejados.</summary>
/// <param name="HasPortugueseWithoutRegion">Há legenda "pt"/"por" sem região: pode ser pt-PT ou pt-BR.</param>
/// <param name="HasUnknownLanguage">Há legenda externa sem idioma no nome.</param>
public sealed record VideoSubtitleState(
    string VideoPath,
    ParsedEpisode? Episode,
    IReadOnlyList<ExistingSubtitle> Existing,
    IReadOnlyList<SubtitleLanguage> Missing,
    bool HasPortugueseWithoutRegion,
    bool HasUnknownLanguage)
{
    /// <summary>A legenda desse idioma talvez já exista com outro rótulo: começa desmarcada na prévia.</summary>
    public string? DoubtFor(SubtitleLanguage language) =>
        HasUnknownLanguage ? "Já existe uma legenda sem idioma no nome; confira se ela já não é esta."
        : HasPortugueseWithoutRegion && language.Code == SubtitleLanguage.PortugueseBrazil ? "Já existe legenda em \"pt\" (sem região); pode ser pt-BR."
        : null;
}

/// <summary>Descobre quais legendas cada vídeo já tem (arquivos externos e faixas do MKV) e quais faltam.</summary>
public static class SubtitleInventory
{
    /// <summary>Idiomas desejados nas configurações, em ordem; com forçadas, cada idioma também pede a versão forçada.</summary>
    public static IReadOnlyList<SubtitleLanguage> WantedFrom(AppSettings settings)
    {
        var codes = SubtitleLanguages.NormalizeList(settings.SubtitleLanguages);
        return
        [
            .. codes.Select(c => new SubtitleLanguage(c)),
            .. settings.DownloadForcedSubtitles ? codes.Select(c => new SubtitleLanguage(c, Forced: true)) : [],
        ];
    }

    public static IReadOnlyList<VideoSubtitleState> Build(
        LibraryFolder folder, IReadOnlyList<SubtitleLanguage> wanted, SubtitleLanguageMode mode, bool checkEmbedded)
    {
        var external = SubtitleMatcher.Assign(folder.Videos, folder.Subtitles);
        return [.. folder.Videos.Select(video => ForVideo(folder.Kind, video, external[video], wanted, mode, checkEmbedded))];
    }

    internal static VideoSubtitleState ForVideo(MediaKind kind, string video, IReadOnlyList<ExternalSubtitle> external,
        IReadOnlyList<SubtitleLanguage> wanted, SubtitleLanguageMode mode, bool checkEmbedded)
    {
        var existing = new List<ExistingSubtitle>();
        var unknown = false;

        foreach (var subtitle in external)
        {
            if (FromExternal(subtitle) is { } language) existing.Add(new ExistingSubtitle(language, subtitle.Path, Embedded: false));
            else unknown = true;
        }

        if (checkEmbedded && MatroskaReader.IsMatroska(video))
        {
            foreach (var track in MatroskaReader.ReadTracks(video))
            {
                if (FromTrack(track) is { } language) existing.Add(new ExistingSubtitle(language, null, Embedded: true));
            }
        }

        var episode = kind == MediaKind.Series ? ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(video)) : null;
        var portugueseWithoutRegion = existing.Any(e => e.Language.Code == "pt" && !e.Language.Forced);
        return new VideoSubtitleState(video, episode, existing, Missing(existing, wanted, mode), portugueseWithoutRegion, unknown);
    }

    /// <summary>
    /// <see cref="SubtitleLanguageMode.All"/>: cada idioma desejado ausente.
    /// <see cref="SubtitleLanguageMode.FirstAvailable"/>: os idiomas com prioridade maior que o melhor já presente
    /// (com pt-BR presente nada falta; com só en presente ainda falta pt-BR). Forçadas e normais são avaliadas à parte.
    /// </summary>
    private static List<SubtitleLanguage> Missing(List<ExistingSubtitle> existing, IReadOnlyList<SubtitleLanguage> wanted, SubtitleLanguageMode mode)
    {
        bool Has(SubtitleLanguage language) => existing.Any(e => e.Language.SameKind(language));

        if (mode == SubtitleLanguageMode.All) return [.. wanted.Where(w => !Has(w))];

        var missing = new List<SubtitleLanguage>();
        foreach (var group in wanted.GroupBy(w => w.Forced))
        {
            foreach (var language in group)
            {
                if (Has(language)) break;
                missing.Add(language);
            }
        }
        return missing;
    }

    internal static SubtitleLanguage? FromExternal(ExternalSubtitle subtitle) =>
        subtitle.Language is null ? null
            : new SubtitleLanguage(SubtitleLanguages.Normalize(subtitle.Language), subtitle.Flag == "forced", subtitle.Flag == "sdh");

    /// <summary>
    /// Idioma de uma faixa de legenda: LanguageBCP47 ("pt-BR") vale mais que Language ("por"); "por" só vira pt-BR
    /// se o nome da faixa indicar o Brasil. Sem idioma ("und"), tenta pelo nome ("English").
    /// </summary>
    internal static SubtitleLanguage? FromTrack(MatroskaTrack track)
    {
        if (!track.IsSubtitle) return null;

        var name = track.Name ?? "";
        var (nameLanguage, nameFlag) = SubtitleMatcher.DetectLanguage(name);

        string? code;
        if (!string.IsNullOrWhiteSpace(track.LanguageBcp47) && !track.LanguageBcp47.Equals("und", StringComparison.OrdinalIgnoreCase))
            code = SubtitleLanguages.Normalize(track.LanguageBcp47);
        else if (track.Language.Equals("und", StringComparison.OrdinalIgnoreCase) || track.Language.Length == 0)
            code = nameLanguage is null ? null : SubtitleLanguages.Normalize(nameLanguage);
        else
            code = SubtitleLanguages.Normalize(track.Language);

        if (code is null) return null;
        if (code == "pt" && SubtitleMatcher.MentionsBrazil(name)) code = SubtitleLanguage.PortugueseBrazil;

        return new SubtitleLanguage(code, track.Forced || nameFlag == "forced", track.HearingImpaired || nameFlag == "sdh");
    }
}
