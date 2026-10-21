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
                var special = folder.SpecialVideos.Contains(video)
                    ? await PlanUnnumberedSpecialAsync(video, folder, show, episodes, ct).ConfigureAwait(false)
                    : null;
                if (special is not null) videoTargets[video] = Path.GetFileNameWithoutExtension(special.TargetPath);
                items.Add(special ?? new RenameItem(ItemKind.Video, video, video, ItemStatus.Skipped, "Temporada/episódio não identificado no nome."));
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

    /// <summary>
    /// Especial sem número (ex.: "Pilot Extended" dentro de Specials): procura o título na temporada 0.
    /// Por ser uma suposição, o item fica com aviso e precisa ser marcado pelo usuário.
    /// </summary>
    private static async Task<RenameItem?> PlanUnnumberedSpecialAsync(
        string video, LibraryFolder folder, MediaCandidate show, EpisodeTitles episodes, CancellationToken ct)
    {
        var text = ReleaseNameParser.ParseSpecialTitle(Path.GetFileNameWithoutExtension(video),
            [folder.Name, show.PreferredTitle, show.Title, show.OriginalTitle]);
        if (text is null) return null;

        if (await episodes.FindByTitleAsync(0, text, ct).ConfigureAwait(false) is not { } episode) return null;

        var title = await episodes.GetAsync(0, episode, ct).ConfigureAwait(false);
        var baseName = NameFormatter.EpisodeBaseName(show.PreferredTitle, 0, episode, null, title);
        var target = Path.Combine(Path.GetDirectoryName(video)!, baseName + Path.GetExtension(video));

        var item = new RenameItem(ItemKind.Video, video, target, ItemStatus.Ok).WithTarget(target);
        return item.IsChange
            ? item with { Status = ItemStatus.Warning, Message = "Especial identificado pelo título; confira antes de aplicar.", NeedsConfirmation = true }
            : item;
    }
}
