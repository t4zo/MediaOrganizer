namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Um site de legendas. Buscar não gasta a cota diária; baixar gasta.
/// Erros de cota, limite e autenticação chegam como <see cref="SubtitleProviderException"/>.
/// </summary>
public interface ISubtitleProvider
{
    /// <summary>Nome exibido ("OpenSubtitles", "SubDL", "SubSource").</summary>
    string Name { get; }

    /// <summary>Ativado e com as credenciais necessárias.</summary>
    bool IsConfigured { get; }

    /// <summary>Downloads restantes hoje, quando o provedor informa.</summary>
    int? RemainingDownloads { get; }

    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct);

    Task<SubtitleFile> DownloadAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct);
}
