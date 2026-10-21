using System.Net.Sockets;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Para cada série/filme: descobre as legendas que faltam e busca candidatos nos sites, na ordem
/// OpenSubtitles → SubDL → SubSource. Só busca (não gasta cota); baixar é com o <see cref="SubtitleDownloader"/>.
/// Usa as correspondências memorizadas pela tela Organizar, como a Coleção.
/// </summary>
public sealed class SubtitleAnalyzer(TmdbClient tmdb, SettingsStore settings, SubtitleProviderSet providers)
{
    private const int MaxParallelFolders = 4;

    /// <param name="refresh">Ignora o cache de buscas (legendas novas para episódios recentes).</param>
    public IAsyncEnumerable<FolderSubtitlePlan> AnalyzeAsync(IReadOnlyList<LibraryFolder> folders, bool refresh, CancellationToken ct = default) =>
        AsyncParallel.StreamAsync(folders, MaxParallelFolders, (folder, token) => AnalyzeFolderAsync(folder, refresh, token), ct);

    public async Task<FolderSubtitlePlan> AnalyzeFolderAsync(LibraryFolder folder, bool refresh, CancellationToken ct)
    {
        if (settings.GetMatch(folder.MatchKey) is not { } id)
            return FolderSubtitlePlan.Failed(folder, FolderSubtitleStatus.NoMatch,
                "Ainda não identificado. Use \"Identificar\" ou analise a pasta na tela Organizar.");

        var current = settings.Current;
        var wanted = SubtitleInventory.WantedFrom(current);
        if (wanted.Count == 0)
            return FolderSubtitlePlan.Failed(folder, FolderSubtitleStatus.Error, "Nenhum idioma de legenda escolhido nas Configurações.");

        try
        {
            var (match, imdbId) = await LookupAsync(folder.Kind, id, ct).ConfigureAwait(false);
            if (match is null)
                return FolderSubtitlePlan.Failed(folder, FolderSubtitleStatus.NoMatch, "O título memorizado não existe mais no TMDb. Identifique-o novamente.");

            var states = await Task.Run(() => SubtitleInventory.Build(folder, wanted, current.SubtitleLanguageMode, current.CheckEmbeddedSubtitles), ct)
                .ConfigureAwait(false);

            var videos = new List<VideoSubtitlePlan>();
            foreach (var state in states.OrderBy(s => s.VideoPath, StringComparer.OrdinalIgnoreCase))
                videos.Add(await PlanVideoAsync(folder.Kind, state, id, imdbId, current, refresh, ct).ConfigureAwait(false));

            var status = videos.Any(v => v.Items.Count > 0) ? FolderSubtitleStatus.Missing : FolderSubtitleStatus.Complete;
            return new FolderSubtitlePlan(folder, match, videos, status);
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return FolderSubtitlePlan.Failed(folder, FolderSubtitleStatus.Error, $"Falha ao consultar o TMDb: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FolderSubtitlePlan.Failed(folder, FolderSubtitleStatus.Error, $"Erro de acesso aos arquivos: {e.Message}");
        }
    }

    private async Task<(MediaCandidate? Match, string? ImdbId)> LookupAsync(MediaKind kind, int id, CancellationToken ct)
    {
        if (kind == MediaKind.Movie)
        {
            var movie = await tmdb.GetMovieAsync(id, LanguagePolicy.English, ct).ConfigureAwait(false);
            return (movie is null ? null : MediaCandidate.From(movie), movie?.ImdbId);
        }

        var show = await tmdb.GetTvAsync(id, LanguagePolicy.English, ct: ct).ConfigureAwait(false);
        if (show is null) return (null, null);
        var ids = await tmdb.GetTvExternalIdsAsync(id, ct).ConfigureAwait(false);
        return (MediaCandidate.From(show), ids?.ImdbId);
    }

    private async Task<VideoSubtitlePlan> PlanVideoAsync(MediaKind kind, VideoSubtitleState state, int tmdbId, string? imdbId,
        AppSettings current, bool refresh, CancellationToken ct)
    {
        if (state.Missing.Count == 0) return new VideoSubtitlePlan(state, [], null);

        if (kind == MediaKind.Series && state.Episode is null)
        {
            return new VideoSubtitlePlan(state,
                [.. state.Missing.Select(l => new SubtitleItem(state.VideoPath, l, [], SubtitleItemStatus.NotFound,
                    "Temporada/episódio não identificados no nome do arquivo. Organize a pasta primeiro."))],
                null);
        }

        var query = new SubtitleQuery(
            kind,
            state.VideoPath,
            SafeLength(state.VideoPath),
            OpenSubtitlesHash.TryCompute(state.VideoPath),
            tmdbId,
            imdbId,
            state.Episode?.Season,
            state.Episode?.Episode,
            Path.GetFileNameWithoutExtension(state.VideoPath),
            state.Missing,
            refresh);

        var (candidates, error) = await SearchAllAsync(query, ct).ConfigureAwait(false);
        if (!current.IncludeMachineTranslated) candidates = [.. candidates.Where(c => !c.MachineTranslated)];

        var items = current.SubtitleLanguageMode == SubtitleLanguageMode.FirstAvailable
            ? FirstAvailable(state, candidates, current.SubtitleHearingImpaired, query.ReleaseName, error)
            : [.. state.Missing.Select(l => Item(state, l, candidates, current.SubtitleHearingImpaired, query.ReleaseName, error))];
        var wanted = SubtitleInventory.WantedFrom(current).ToList();
        return new VideoSubtitlePlan(state, [.. items.Select(i => i with { Priority = wanted.IndexOf(i.Wanted) })], query);
    }

    /// <summary>
    /// Consulta os sites em ordem. Um idioma deixa de ser pedido aos próximos sites quando já tem candidato
    /// confiável (hash ou ID + episódio). Erros de cota/chave tiram o site de uso nesta sessão.
    /// </summary>
    private async Task<(List<SubtitleCandidate> Candidates, string? Error)> SearchAllAsync(SubtitleQuery query, CancellationToken ct)
    {
        var all = new List<SubtitleCandidate>();
        var remaining = query.Languages.ToList();
        string? lastError = null;

        foreach (var provider in providers.Active)
        {
            if (remaining.Count == 0) break;
            try
            {
                all.AddRange(await provider.SearchAsync(query with { Languages = remaining }, ct).ConfigureAwait(false));
            }
            catch (SubtitleProviderException e)
            {
                providers.Report(e);
                lastError = e.Message;
                continue;
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or HttpRequestException)
            {
                lastError = $"{provider.Name}: resposta inesperada ({e.Message}).";
                continue;
            }

            remaining = [.. remaining.Where(l => !all.Any(c => c.IsStrong && c.Language.SameKind(l)))];
        }

        if (providers.Active.Count == 0 && all.Count == 0)
            lastError ??= providers.Notices() is [var notice, ..] ? notice : "Nenhum site de legendas disponível.";
        return (all, lastError);
    }

    private static SubtitleItem Item(VideoSubtitleState state, SubtitleLanguage language, List<SubtitleCandidate> all,
        HearingImpairedPreference preference, string releaseName, string? error)
    {
        var ranked = SubtitleScorer.Rank(all.Where(c => c.Language.SameKind(language)), releaseName, preference);
        if (ranked.Count == 0)
        {
            return error is not null && all.Count == 0
                ? new SubtitleItem(state.VideoPath, language, [], SubtitleItemStatus.Error, error)
                : new SubtitleItem(state.VideoPath, language, [], SubtitleItemStatus.NotFound, "Nenhum site tem esta legenda ainda.");
        }

        var best = ranked[0];
        if (File.Exists(SubtitleItem.TargetPath(state.VideoPath, best)))
            return new SubtitleItem(state.VideoPath, language, ranked, SubtitleItemStatus.Conflict, "Já existe um arquivo com este nome.");

        var doubt = state.DoubtFor(language);
        var message = doubt ?? (best.IsStrong ? null : "Encontrada só pelo nome: confira a sincronia.");
        return new SubtitleItem(state.VideoPath, language, ranked, SubtitleItemStatus.Found, message, NeedsConfirmation: doubt is not null || !best.IsStrong);
    }

    /// <summary>Por grupo (normais/forçadas): o primeiro idioma da lista que tiver candidato; os demais ficam de fora.</summary>
    private static List<SubtitleItem> FirstAvailable(VideoSubtitleState state, List<SubtitleCandidate> all,
        HearingImpairedPreference preference, string releaseName, string? error)
    {
        var items = new List<SubtitleItem>();
        foreach (var group in state.Missing.GroupBy(l => l.Forced))
        {
            var languages = group.ToList();
            var chosen = languages.FirstOrDefault(l => all.Any(c => c.Language.SameKind(l)));
            if (chosen is null)
            {
                items.Add(Item(state, languages[0], all, preference, releaseName, error));
                continue;
            }

            var item = Item(state, chosen, all, preference, releaseName, error);
            if (chosen != languages[0] && item.Status == SubtitleItemStatus.Found && item.Message is null)
                item = item with { Message = $"Sem legenda {languages[0].Code}; usando {chosen.Code}." };
            items.Add(item);
        }
        return items;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
