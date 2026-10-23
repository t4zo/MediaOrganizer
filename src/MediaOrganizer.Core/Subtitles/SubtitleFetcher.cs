using System.IO.Hashing;
using System.Text;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Legendas já baixadas (<c>data/cache/subtitle-files</c>): a verificação de sincronia baixa as opções, e a escolhida
/// não gasta outro download da cota quando for salva.
/// </summary>
public sealed class SubtitleContentCache(string directory, TimeSpan timeToLive)
{
    public static TimeSpan DefaultTimeToLive { get; } = TimeSpan.FromDays(7);

    public bool Contains(SubtitleCandidate candidate, SubtitleQuery query) => Fresh(FileFor(candidate, query));

    public byte[]? Get(SubtitleCandidate candidate, SubtitleQuery query)
    {
        var file = FileFor(candidate, query);
        if (!Fresh(file)) return null;
        try
        {
            return File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Set(SubtitleCandidate candidate, SubtitleQuery query, byte[] content)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(FileFor(candidate, query), content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sem cache, a legenda só é baixada de novo.
        }
    }

    private bool Fresh(string file) => File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= timeToLive;

    /// <summary>Pacotes de temporada têm o mesmo ID em todos os episódios: a chave inclui o episódio.</summary>
    private string FileFor(SubtitleCandidate candidate, SubtitleQuery query)
    {
        var key = $"{candidate.Provider}|{candidate.ProviderId}|{query.Season}|{query.Episode}";
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(key));
        return Path.Combine(directory, Convert.ToHexStringLower(hash) + "." + candidate.Format);
    }
}

/// <summary>Baixa uma legenda (ou reaproveita do cache) e converte as de texto para UTF-8.</summary>
public sealed class SubtitleFetcher(SubtitleProviderSet providers, SubtitleContentCache? cache = null)
{
    public bool IsCached(SubtitleCandidate candidate, SubtitleQuery query) => cache?.Contains(candidate, query) == true;

    public async Task<byte[]> FetchAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct)
    {
        if (cache?.Get(candidate, query) is { Length: > 0 } cached) return cached;

        var provider = providers.Find(candidate.Provider)
                       ?? throw new IOException($"Site {candidate.Provider} indisponível.");
        if (providers.DisabledReason(provider) is { } reason)
            throw new SubtitleProviderException(provider.Name, SubtitleProviderError.QuotaExceeded, reason);

        var file = await provider.DownloadAsync(candidate, query, ct).ConfigureAwait(false);
        if (file.Content.Length == 0)
            throw new SubtitleProviderException(provider.Name, SubtitleProviderError.InvalidContent, "O site devolveu um arquivo vazio.");

        var content = SubtitleText.IsText(file.Extension) ? SubtitleText.ToUtf8(file.Content) : file.Content;
        cache?.Set(candidate, query, content);
        return content;
    }
}
