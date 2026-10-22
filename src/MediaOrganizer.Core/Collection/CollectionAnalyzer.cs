using System.Net.Sockets;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Collection;

/// <summary>
/// Compara os episódios de cada série da biblioteca com a lista oficial do TMDb e aponta os que faltam.
/// Usa as correspondências memorizadas pela tela Organizar; sem uma, identifica a série com a mesma busca
/// (<paramref name="identifier"/>) e memoriza se for confiável.
/// </summary>
public sealed class CollectionAnalyzer(TmdbClient tmdb, SettingsStore settings, TimeProvider time, LibraryAnalyzer? identifier = null)
{
    private const int MaxParallelSeries = 4;

    /// <param name="refresh">Ignora o cache do TMDb (dados atualizados para séries em exibição).</param>
    public IAsyncEnumerable<SeriesReport> AnalyzeAsync(IReadOnlyList<LibraryFolder> folders, bool refresh, CancellationToken ct = default) =>
        AsyncParallel.StreamAsync(folders, MaxParallelSeries, (folder, token) => AnalyzeSeriesAsync(folder, refresh, token), ct);

    public async Task<SeriesReport> AnalyzeSeriesAsync(LibraryFolder folder, bool refresh, CancellationToken ct)
    {
        try
        {
            var (found, reason) = identifier is null
                ? (settings.GetMatch(folder.MatchKey), "Série ainda não identificada. Use \"Identificar\" ou analise a pasta na tela Organizar.")
                : await identifier.ResolveMatchAsync(folder, ct).ConfigureAwait(false);
            if (found is not { } id) return new SeriesReport(folder, null, [], CollectionStatus.NoMatch, reason ?? "Série ainda não identificada.");

            if (await tmdb.GetTvAsync(id, LanguagePolicy.English, refresh, ct).ConfigureAwait(false) is not { } details)
                return new SeriesReport(folder, null, [], CollectionStatus.NoMatch, "A série memorizada não existe mais no TMDb. Identifique-a novamente.");

            var show = MediaCandidate.From(details);
            var titles = new EpisodeTitles(tmdb, id, LanguagePolicy.TitleLanguage(show.OriginalLanguage), refresh);
            var present = PresentEpisodes(folder);
            var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

            var seasons = new List<SeasonReport>();
            foreach (var season in SeasonNumbers(details, present))
            {
                var report = await AnalyzeSeasonAsync(season, titles, present, today, ct).ConfigureAwait(false);
                if (report.Episodes.Count > 0) seasons.Add(report);
            }

            var status = seasons.Any(s => !s.IsSpecials && !s.IsComplete) ? CollectionStatus.Incomplete : CollectionStatus.Complete;
            return new SeriesReport(folder, show, seasons, status);
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SeriesReport(folder, null, [], CollectionStatus.Error, $"Falha ao consultar o TMDb: {e.Message}");
        }
    }

    private static async Task<SeasonReport> AnalyzeSeasonAsync(
        int season, EpisodeTitles titles, HashSet<(int Season, int Episode)> present, DateOnly today, CancellationToken ct)
    {
        var episodes = new List<EpisodeStatus>();

        foreach (var episode in await titles.EpisodesAsync(season, ct).ConfigureAwait(false))
        {
            var airDate = episode.AiredOn;
            var isAired = airDate is { } date && date <= today;
            var isPresent = present.Contains((season, episode.EpisodeNumber));

            // Sem data ou no futuro: não conta como esperado (nem como faltando).
            var state = (isAired, isPresent) switch
            {
                (true, true) => EpisodeState.Present,
                (true, false) => EpisodeState.Missing,
                (false, true) => EpisodeState.PresentNotAired,
                (false, false) => EpisodeState.Upcoming,
            };

            var title = await titles.GetAsync(season, episode.EpisodeNumber, ct).ConfigureAwait(false);
            episodes.Add(new EpisodeStatus(new EpisodeRef(season, episode.EpisodeNumber, title, airDate), state));
        }

        return new SeasonReport(season, episodes);
    }

    /// <summary>Temporadas oficiais (com os especiais apenas se a opção estiver ligada) mais as que existem nos arquivos.</summary>
    private IEnumerable<int> SeasonNumbers(TvDetails details, HashSet<(int Season, int Episode)> present)
    {
        var official = details.Seasons?.Select(s => s.SeasonNumber) ?? Enumerable.Range(1, details.NumberOfSeasons);
        return official
            .Concat(present.Select(p => p.Season))
            .Where(s => s > 0 || settings.Current.IncludeSpecials)
            .Distinct()
            .Order();
    }

    /// <summary>Episódios presentes na pasta; "S04E01-E02" conta como dois.</summary>
    private static HashSet<(int Season, int Episode)> PresentEpisodes(LibraryFolder folder)
    {
        var present = new HashSet<(int, int)>();
        foreach (var video in folder.Videos)
        {
            if (ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(video)) is not { } parsed) continue;
            for (var e = parsed.Episode; e <= (parsed.LastEpisode ?? parsed.Episode); e++) present.Add((parsed.Season, e));
        }
        return present;
    }
}
