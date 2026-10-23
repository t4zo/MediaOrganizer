namespace MediaOrganizer.Core.Subtitles;

/// <summary>Idioma de legenda no formato usado nos nomes de arquivo ("pt-BR", "en"), com as flags.</summary>
public sealed record SubtitleLanguage(string Code, bool Forced = false, bool HearingImpaired = false)
{
    public const string PortugueseBrazil = "pt-BR";
    public const string English = "en";

    /// <summary>Mesmo idioma e mesmo tipo (forçada ou normal); SDH conta como normal.</summary>
    public bool SameKind(SubtitleLanguage other) =>
        string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase) && Forced == other.Forced;

    /// <summary>Sufixo do nome do arquivo: ".pt-BR", ".en.forced", ".en.sdh".</summary>
    public string FileSuffix => "." + Code + (Forced ? ".forced" : HearingImpaired ? ".sdh" : "");

    public string DisplayName => SubtitleLanguages.DisplayName(Code) + (Forced ? " (forçada)" : HearingImpaired ? " (SDH)" : "");

    public override string ToString() => Code + (Forced ? ".forced" : HearingImpaired ? ".sdh" : "");
}

public enum HearingImpairedPreference
{
    /// <summary>Prefere legendas sem descrições de sons (padrão).</summary>
    Avoid,
    /// <summary>Prefere legendas para surdos e ensurdecidos (SDH).</summary>
    Prefer,
    /// <summary>Indiferente.</summary>
    Any,
}

public enum SubtitleLanguageMode
{
    /// <summary>Baixa cada idioma da lista que estiver faltando.</summary>
    All,
    /// <summary>Só o primeiro idioma da lista que for encontrado (os seguintes servem de alternativa).</summary>
    FirstAvailable,
}

public enum SubtitleOutput
{
    /// <summary>Arquivo ao lado do vídeo (oculto por padrão).</summary>
    File,
    /// <summary>Faixa embutida no MKV (requer o mkvmerge).</summary>
    Embed,
}

/// <summary>O que se sabe do vídeo para buscar legendas.</summary>
/// <param name="FileHash">Hash do OpenSubtitles (<see cref="OpenSubtitlesHash"/>), ou <c>null</c> se não foi possível ler.</param>
/// <param name="ImdbId">Do filme, ou da série no caso de episódios ("tt0944947").</param>
/// <param name="ReleaseName">Nome do arquivo sem extensão (grupo, fonte e resolução ajudam a pontuar).</param>
/// <param name="Refresh">Ignora o cache de buscas.</param>
public sealed record SubtitleQuery(
    MediaKind Kind,
    string VideoPath,
    long FileSize,
    string? FileHash,
    int TmdbId,
    string? ImdbId,
    int? Season,
    int? Episode,
    string ReleaseName,
    IReadOnlyList<SubtitleLanguage> Languages,
    bool Refresh = false);

/// <summary>Uma legenda encontrada num provedor.</summary>
/// <param name="ProviderId">Identificador para o download (file_id, url...).</param>
/// <param name="HashMatch">O provedor confirmou o hash do arquivo (sincronia praticamente garantida).</param>
/// <param name="IdMatch">Encontrada pelo ID do título (e temporada/episódio, para séries), não pelo nome.</param>
/// <param name="IsSeasonPack">Pacote com a temporada inteira (o episódio é extraído do ZIP).</param>
public sealed record SubtitleCandidate(
    string Provider,
    string ProviderId,
    SubtitleLanguage Language,
    string? ReleaseName,
    bool HashMatch,
    bool IdMatch,
    bool IsSeasonPack,
    int Downloads,
    string Format = "srt",
    bool MachineTranslated = false)
{
    public int Score { get; init; }

    /// <summary>Casamento confiável: hash ou ID + episódio.</summary>
    public bool IsStrong => HashMatch || IdMatch;
}

/// <summary>Conteúdo baixado (texto ainda na codificação original).</summary>
public sealed record SubtitleFile(byte[] Content, string Extension);
