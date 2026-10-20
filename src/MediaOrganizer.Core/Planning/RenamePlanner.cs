using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Planning;

/// <summary>Gera o plano de renomeação de uma pasta já identificada no TMDb.</summary>
public sealed class RenamePlanner(TmdbClient tmdb)
{
    public async Task<FolderPlan> PlanSeriesAsync(LibraryFolder folder, MediaCandidate show, bool lowConfidence, bool renameSubtitles, CancellationToken ct)
    {
        var language = LanguagePolicy.TitleLanguage(show.OriginalLanguage);
        var showTitle = show.PreferredTitle;
        var episodes = new EpisodeTitles(tmdb, show.Id, language);

        var items = new List<RenameItem>();
        var videoTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var video in folder.Videos)
        {
            var parsed = ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(video));
            if (parsed is null)
            {
                items.Add(new RenameItem(ItemKind.Video, video, video, ItemStatus.Skipped, "Temporada/episódio não identificado no nome."));
                continue;
            }

            var titles = new List<string>();
            var missing = false;
            for (var e = parsed.Episode; e <= (parsed.LastEpisode ?? parsed.Episode); e++)
            {
                var title = await episodes.GetAsync(parsed.Season, e, ct).ConfigureAwait(false);
                if (title is null) missing = true;
                else if (!titles.Contains(title)) titles.Add(title);
            }

            // Sem título oficial: usa o que veio no arquivo.
            var episodeTitle = missing && titles.Count == 0 ? parsed.EpisodeTitle : string.Join(" & ", titles);
            var baseName = NameFormatter.EpisodeBaseName(showTitle, parsed.Season, parsed.Episode, parsed.LastEpisode, episodeTitle);
            var target = Path.Combine(Path.GetDirectoryName(video)!, baseName + Path.GetExtension(video));
            videoTargets[video] = baseName;

            var item = new RenameItem(ItemKind.Video, video, target, ItemStatus.Ok).WithTarget(target);
            if (missing && item.IsChange)
            {
                var code = $"S{parsed.Season:00}E{parsed.Episode:00}";
                item = item with
                {
                    Status = ItemStatus.Warning,
                    Message = episodeTitle is null
                        ? $"{code} não encontrado no TMDb; renomeado sem título."
                        : $"{code} não encontrado no TMDb; mantido o título do arquivo.",
                };
            }
            items.Add(item);
        }

        if (renameSubtitles) items.AddRange(SubtitleMatcher.Plan(videoTargets, folder.Subtitles));

        var checkedItems = ConflictDetector.Apply(items);
        return new FolderPlan(folder, show, lowConfidence, checkedItems, FolderPlan.StatusFor(checkedItems, lowConfidence));
    }

    public static FolderPlan PlanMovie(LibraryFolder folder, MediaCandidate movie, bool lowConfidence, bool renameSubtitles)
    {
        var baseName = NameFormatter.MovieBaseName(movie.PreferredTitle, movie.Year);
        var items = new List<RenameItem>();
        var video = folder.Videos[0];

        // Arquivo solto ganha uma pasta própria; arquivo em pasta é renomeado no lugar e a pasta é renomeada por último.
        var videoDir = folder.IsLooseFile ? Path.Combine(folder.RootPath, baseName) : Path.GetDirectoryName(video)!;
        var videoTarget = Path.Combine(videoDir, baseName + Path.GetExtension(video));
        items.Add(new RenameItem(ItemKind.Video, video, videoTarget, ItemStatus.Ok).WithTarget(videoTarget));

        if (renameSubtitles)
        {
            var subtitles = SubtitleMatcher.Plan(new Dictionary<string, string> { [video] = baseName }, folder.Subtitles);
            items.AddRange(folder.IsLooseFile
                ? subtitles.Select(s => s.IsChange || s.Status == ItemStatus.Unchanged
                    ? s.WithTarget(Path.Combine(videoDir, Path.GetFileName(s.TargetPath)))
                    : s)
                : subtitles);
        }

        if (!folder.IsLooseFile)
        {
            var folderTarget = Path.Combine(Path.GetDirectoryName(folder.Path)!, baseName);
            items.Add(new RenameItem(ItemKind.Folder, folder.Path, folderTarget, ItemStatus.Ok).WithTarget(folderTarget));
        }

        var checkedItems = ConflictDetector.Apply(items);
        return new FolderPlan(folder, movie, lowConfidence, checkedItems, FolderPlan.StatusFor(checkedItems, lowConfidence));
    }

    /// <summary>Títulos dos episódios de uma série, por temporada, com fallback para inglês.</summary>
    private sealed class EpisodeTitles(TmdbClient tmdb, int showId, string language)
    {
        private readonly Dictionary<(int Season, string Language), IReadOnlyDictionary<int, string?>> _seasons = [];

        public async Task<string?> GetAsync(int season, int episode, CancellationToken ct)
        {
            var title = (await SeasonAsync(season, language, ct).ConfigureAwait(false)).GetValueOrDefault(episode);
            if (!LanguagePolicy.IsGenericEpisodeTitle(title) || language == LanguagePolicy.English) return Clean(title);

            var english = (await SeasonAsync(season, LanguagePolicy.English, ct).ConfigureAwait(false)).GetValueOrDefault(episode);
            return Clean(LanguagePolicy.IsGenericEpisodeTitle(english) ? title : english);
        }

        private async Task<IReadOnlyDictionary<int, string?>> SeasonAsync(int season, string lang, CancellationToken ct)
        {
            if (_seasons.TryGetValue((season, lang), out var cached)) return cached;

            var data = await tmdb.GetSeasonAsync(showId, season, lang, ct).ConfigureAwait(false);
            var map = data?.Episodes
                .GroupBy(e => e.EpisodeNumber)
                .ToDictionary(g => g.Key, g => g.First().Name) ?? [];
            _seasons[(season, lang)] = map;
            return map;
        }

        private static string? Clean(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }
}
