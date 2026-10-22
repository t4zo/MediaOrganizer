namespace MediaOrganizer.Core.Subtitles;

/// <summary>Resultado de uma opção: a comparação, ou por que não foi possível comparar.</summary>
/// <param name="CanRetry">Falha passageira (cota, rede): a opção continua contando como não verificada.</param>
public sealed record CandidateSync(SubtitleCandidate Candidate, SyncResult? Result, string? Error = null, bool CanRetry = false);

/// <summary>Verificação de um vídeo. <see cref="Message"/> explica quando nada pôde ser verificado.</summary>
public sealed record VideoSyncReport(SyncReference? Reference, IReadOnlyList<CandidateSync> Candidates, string? Message = null);

/// <summary>
/// "Verificar sincronia": baixa as opções de cada legenda que falta (gasta cota, mas o arquivo fica em cache para
/// quando for salvo) e compara os tempos com uma legenda que o vídeo já tem.
/// </summary>
/// <param name="openSubtitlesLimit">Opções do OpenSubtitles por clique (configurável; a cota dele é pequena).</param>
public sealed class SubtitleSyncChecker(SubtitleProviderSet providers, SubtitleReferenceProvider references, SubtitleContentCache? cache = null,
    Func<int>? openSubtitlesLimit = null)
{
    /// <summary>Padrão de opções do OpenSubtitles baixadas por clique ("Verificar mais" pega as seguintes).</summary>
    public const int DefaultOpenSubtitlesLimit = 5;

    public const int MaxOpenSubtitlesLimit = 50;

    private readonly SubtitleFetcher _fetcher = new(providers, cache);

    public SyncReferenceChoice Reference(VideoSubtitlePlan video) => references.Choose(video.State);

    public int OpenSubtitlesLimit => Math.Clamp(openSubtitlesLimit?.Invoke() ?? DefaultOpenSubtitlesLimit, 1, MaxOpenSubtitlesLimit);

    /// <summary>Identifica uma opção entre buscas (o registro é recriado quando a lista é reordenada).</summary>
    public static string Key(SubtitleCandidate candidate) => candidate.Provider + ":" + candidate.ProviderId;

    /// <summary>
    /// As opções ainda não verificadas (<paramref name="verified"/>), na ordem da pontuação. Só as do OpenSubtitles
    /// têm limite por clique (<paramref name="openSubtitlesLimit"/>), por causa da cota pequena; as com hash e as
    /// de imagem não contam, porque não são baixadas. SubDL e SubSource entram todas.
    /// </summary>
    public static IEnumerable<SubtitleCandidate> ToCheck(SubtitleItem item, int openSubtitlesLimit, IReadOnlySet<string>? verified = null)
    {
        var limited = 0;
        foreach (var candidate in item.Candidates)
        {
            if (verified?.Contains(Key(candidate)) == true) continue;
            if (IsLimited(candidate))
            {
                if (limited >= openSubtitlesLimit) continue;
                limited++;
            }
            yield return candidate;
        }
    }

    private static bool IsLimited(SubtitleCandidate candidate) =>
        candidate.Provider.Equals(Providers.OpenSubtitlesProvider.ProviderName, StringComparison.OrdinalIgnoreCase) &&
        !candidate.HashMatch && SubtitleText.IsText(candidate.Format);

    /// <summary>Opções que ainda faltam verificar, em todas as legendas do vídeo.</summary>
    public static int Unverified(VideoSubtitlePlan video, IReadOnlySet<string> verified) =>
        video.Items.Sum(i => i.Candidates.Count(c => !verified.Contains(Key(c))));

    /// <summary>Downloads que a verificação vai gastar, por site: as com hash, as de imagem e as já baixadas não contam.</summary>
    public IReadOnlyList<(string Provider, int Count)> DownloadsNeeded(VideoSubtitlePlan video, IReadOnlySet<string>? verified = null) =>
        video.Query is not { } query
            ? []
            : [.. video.Items.SelectMany(i => ToCheck(i, OpenSubtitlesLimit, verified))
                .Where(c => !c.HashMatch && SubtitleText.IsText(c.Format) && !_fetcher.IsCached(c, query))
                .GroupBy(c => c.Provider, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count()))];

    /// <param name="verified">Opções já verificadas (<see cref="Key"/>): "Verificar mais" pega as próximas.</param>
    public async Task<VideoSyncReport> CheckAsync(VideoSubtitlePlan video, IProgress<double>? progress, CancellationToken ct,
        IReadOnlySet<string>? verified = null)
    {
        if (video.Query is not { } query) return new VideoSyncReport(null, [], "Este vídeo não foi identificado.");

        var choice = references.Choose(video.State);
        if (choice.Source is not { } source) return new VideoSyncReport(null, [], choice.Reason);

        var candidates = video.Items.SelectMany(i => ToCheck(i, OpenSubtitlesLimit, verified)).ToList();
        var steps = candidates.Count + 1.0;

        SyncReference reference;
        try
        {
            var referenceProgress = progress is null ? null : new Progress<double>(p => progress.Report(p / steps));
            reference = await references.LoadAsync(video.State.VideoPath, source, referenceProgress, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new VideoSyncReport(null, [], $"Não foi possível ler a legenda de referência: {e.Message}");
        }
        if (reference.Cues.Count < SubtitleSync.MinCues)
            return new VideoSyncReport(reference, [], $"A legenda de referência ({reference.Description}) tem falas de menos para comparar.");

        var results = new List<CandidateSync>();
        for (var i = 0; i < candidates.Count; i++)
        {
            results.Add(await CheckAsync(candidates[i], query, reference, ct).ConfigureAwait(false));
            progress?.Report((i + 2) / steps);
        }
        return new VideoSyncReport(reference, results);
    }

    private async Task<CandidateSync> CheckAsync(SubtitleCandidate candidate, SubtitleQuery query, SyncReference reference, CancellationToken ct)
    {
        if (candidate.HashMatch) return new CandidateSync(candidate, SyncResult.HashMatch);
        if (!SubtitleText.IsText(candidate.Format)) return new CandidateSync(candidate, null, "Legenda em imagem: não tem tempos de texto para comparar.");

        try
        {
            var content = await _fetcher.FetchAsync(candidate, query, ct).ConfigureAwait(false);
            var result = SubtitleSync.Compare(SubtitleCues.Parse(content), reference.Cues);
            return new CandidateSync(candidate, result, result is null ? "A legenda baixada está vazia ou não pôde ser lida." : null);
        }
        catch (SubtitleProviderException e)
        {
            providers.Report(e);
            return new CandidateSync(candidate, null, e.Message, CanRetry: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new CandidateSync(candidate, null, e.Message, CanRetry: true);
        }
    }
}
