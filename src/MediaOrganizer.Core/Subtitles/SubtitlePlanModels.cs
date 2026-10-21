using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Subtitles;

public enum SubtitleItemStatus
{
    /// <summary>Há candidato para baixar.</summary>
    Found,
    /// <summary>Nenhum site tem esta legenda.</summary>
    NotFound,
    /// <summary>Já existe um arquivo com o nome de destino.</summary>
    Conflict,
    /// <summary>Falha ao consultar os sites.</summary>
    Error,
}

/// <summary>Uma legenda que falta num vídeo e os candidatos encontrados (melhor primeiro).</summary>
/// <param name="NeedsConfirmation">Começa desmarcada: casamento fraco ou a legenda talvez já exista com outro rótulo.</param>
public sealed record SubtitleItem(
    string VideoPath,
    SubtitleLanguage Wanted,
    IReadOnlyList<SubtitleCandidate> Candidates,
    SubtitleItemStatus Status,
    string? Message = null,
    bool NeedsConfirmation = false)
{
    public SubtitleCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>Arquivo final de um candidato: "&lt;vídeo&gt;.pt-BR[.forced|.sdh].srt", na pasta do vídeo.</summary>
    public static string TargetPath(string videoPath, SubtitleCandidate candidate) =>
        Path.Combine(Path.GetDirectoryName(videoPath)!,
            Path.GetFileNameWithoutExtension(videoPath) + candidate.Language.FileSuffix + "." + candidate.Format);
}

/// <summary>Legendas de um vídeo: o que já tem, o que falta e a consulta usada nos sites.</summary>
public sealed record VideoSubtitlePlan(VideoSubtitleState State, IReadOnlyList<SubtitleItem> Items, SubtitleQuery? Query);

public enum FolderSubtitleStatus
{
    /// <summary>Faltam legendas (encontradas ou não).</summary>
    Missing,
    /// <summary>Todos os vídeos têm os idiomas desejados.</summary>
    Complete,
    /// <summary>Pasta sem correspondência com o TMDb.</summary>
    NoMatch,
    /// <summary>Falha ao consultar o TMDb ou ler a pasta.</summary>
    Error,
}

/// <summary>Resultado da tela Legendas para uma série ou filme.</summary>
public sealed record FolderSubtitlePlan(
    LibraryFolder Folder,
    MediaCandidate? Match,
    IReadOnlyList<VideoSubtitlePlan> Videos,
    FolderSubtitleStatus Status,
    string? Message = null)
{
    public IEnumerable<SubtitleItem> Items => Videos.SelectMany(v => v.Items);

    public int MissingCount => Items.Count();
    public int FoundCount => Items.Count(i => i.Status == SubtitleItemStatus.Found);

    /// <summary>Faltantes por idioma, na ordem das configurações: ("pt-BR", 8), ("en", 3).</summary>
    public IReadOnlyList<(string Language, int Count)> MissingByLanguage =>
        [.. Items.GroupBy(i => i.Wanted.ToString()).Select(g => (g.Key, g.Count()))];

    public static FolderSubtitlePlan Failed(LibraryFolder folder, FolderSubtitleStatus status, string message, MediaCandidate? match = null) =>
        new(folder, match, [], status, message);
}
