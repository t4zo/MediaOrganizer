using System.Net.Sockets;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core;

public sealed record AnalyzeOptions(bool RenameSubtitles, bool Force);

/// <summary>
/// Orquestra o fluxo Scan → (fingerprint) → Match → Plan para cada pasta da biblioteca.
/// É a fachada usada pela interface.
/// </summary>
public sealed class LibraryAnalyzer(MediaMatcher matcher, RenamePlanner planner, SettingsStore settings, ProcessedStore processed)
{
    private const int MaxParallelFolders = 4;

    public LibraryScanner CreateScanner() =>
        new(settings.Current.IgnoredFolders, new JunkDetector(settings.Current.JunkPatterns), settings.Current.IncludeSpecials);

    public IReadOnlyList<LibraryFolder> Scan(string root, MediaKind kind) => CreateScanner().Scan(root, kind);

    /// <summary>Analisa as pastas em paralelo e devolve cada plano assim que fica pronto.</summary>
    public IAsyncEnumerable<FolderPlan> AnalyzeAsync(IReadOnlyList<LibraryFolder> folders, AnalyzeOptions options, CancellationToken ct = default) =>
        AsyncParallel.StreamAsync(folders, MaxParallelFolders, (folder, token) => AnalyzeFolderAsync(folder, options, null, token), ct);

    /// <summary>Analisa uma pasta. Com <paramref name="chosen"/>, usa a correspondência escolhida pelo usuário.</summary>
    public async Task<FolderPlan> AnalyzeFolderAsync(LibraryFolder folder, AnalyzeOptions options, MediaCandidate? chosen, CancellationToken ct)
    {
        if (folder.Videos.Count == 0)
            return FolderPlan.Failed(folder, FolderStatus.NoChanges, "Nenhum vídeo nesta pasta.");

        var knownId = settings.GetMatch(folder.MatchKey);
        if (chosen is null && !options.Force && knownId is not null &&
            processed.Get(folder.Key)?.TmdbId == knownId && processed.IsUnchanged(folder))
        {
            // Sem consultar o TMDb: lista os arquivos como estão, só para exibição.
            IReadOnlyList<RenameItem> unchanged =
            [
                .. folder.Videos.Select(v => new RenameItem(ItemKind.Video, v, v, ItemStatus.Unchanged)),
                .. folder.Subtitles.Select(s => new RenameItem(ItemKind.Subtitle, s, s, ItemStatus.Unchanged)),
            ];
            return new FolderPlan(folder, null, false, unchanged, FolderStatus.AlreadyProcessed,
                "Nada foi alterado desde o último processamento.");
        }

        try
        {
            var match = chosen is not null ? new MatchResult(chosen, LowConfidence: false)
                : knownId is { } id && await matcher.GetDetailsAsync(folder.Kind, id, ct).ConfigureAwait(false) is { } known ? new MatchResult(known, false)
                : await FindMatchAsync(folder, ct).ConfigureAwait(false);

            if (match is null)
                return FolderPlan.Failed(folder, FolderStatus.NotFound, "Não encontrado no TMDb. Use \"Alterar correspondência\" para buscar manualmente.");

            if (chosen is not null) settings.SetMatch(folder.MatchKey, chosen.Id);

            var plan = folder.Kind == MediaKind.Series
                ? await planner.PlanSeriesAsync(folder, match.Candidate, match.LowConfidence, options.RenameSubtitles, ct).ConfigureAwait(false)
                : RenamePlanner.PlanMovie(folder, match.Candidate, match.LowConfidence, options.RenameSubtitles);

            if (!match.LowConfidence && knownId != match.Candidate.Id) settings.SetMatch(folder.MatchKey, match.Candidate.Id);
            if (plan.Status == FolderStatus.NoChanges && !folder.IsLooseFile) processed.MarkProcessed(folder, match.Candidate.Id);

            return plan;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return FolderPlan.Failed(folder, FolderStatus.Error, $"Falha ao consultar o TMDb: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FolderPlan.Failed(folder, FolderStatus.Error, $"Erro de acesso aos arquivos: {e.Message}");
        }
    }

    /// <summary>
    /// Depois de aplicar: se todas as mudanças da pasta foram aplicadas, grava a correspondência e o fingerprint
    /// para que a próxima análise informe "nada foi alterado".
    /// </summary>
    /// <returns>A chave da pasta no estado final (muda quando um filme ganha pasta/novo nome), ou <c>null</c>.</returns>
    public string? RecordApplied(FolderPlan plan, IReadOnlyCollection<RenameItem> applied)
    {
        if (plan.Match is null) return null;
        settings.SetMatch(plan.Folder.MatchKey, plan.Match.Id);

        var appliedSources = applied.Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!plan.Items.Where(i => i.IsChange).All(i => appliedSources.Contains(i.SourcePath))) return null;

        var folderPath = plan.Folder switch
        {
            { IsLooseFile: true } => Path.GetDirectoryName(applied.First(i => i.Kind == ItemKind.Video).TargetPath)!,
            _ => applied.FirstOrDefault(i => i.Kind == ItemKind.Folder)?.TargetPath ?? plan.Folder.Path,
        };

        var rescanned = CreateScanner().ScanFolder(plan.Folder.Kind, plan.Folder.RootPath, folderPath);
        settings.SetMatch(rescanned.MatchKey, plan.Match.Id);
        processed.MarkProcessed(rescanned, plan.Match.Id);
        return rescanned.Key;
    }

    public void ForgetProcessed(IEnumerable<string> folderKeys) => processed.Remove(folderKeys);

    /// <summary>
    /// Correspondência da pasta para as telas Coleção e Legendas: a memorizada ou, sem ela, a mesma busca da tela
    /// Organizar. Só uma correspondência confiável é memorizada e usada; incerta, devolve a mensagem para "Identificar".
    /// </summary>
    /// <returns>O id do TMDb, ou <c>null</c> com o motivo.</returns>
    public async Task<(int? Id, string? Message)> ResolveMatchAsync(LibraryFolder folder, CancellationToken ct)
    {
        if (settings.GetMatch(folder.MatchKey) is { } id) return (id, null);
        if (folder.Videos.Count == 0) return (null, "Nenhum vídeo nesta pasta.");

        var match = await FindMatchAsync(folder, ct).ConfigureAwait(false);
        if (match is null) return (null, "Não encontrado no TMDb. Use \"Identificar\" para buscar manualmente.");
        if (match.LowConfidence)
            return (null, $"Não foi possível identificar com certeza (talvez \"{match.Candidate.DisplayName}\"). Use \"Identificar\" para confirmar.");

        settings.SetMatch(folder.MatchKey, match.Candidate.Id);
        return (match.Candidate.Id, null);
    }

    private Task<MatchResult?> FindMatchAsync(LibraryFolder folder, CancellationToken ct)
    {
        var (folderTitle, folderYear) = CleanName(folder.Name);

        if (folder.Kind == MediaKind.Movie)
        {
            var fromFolder = ReleaseNameParser.ParseMovie(folder.Name);
            var fromFile = ReleaseNameParser.ParseMovie(Path.GetFileNameWithoutExtension(folder.Videos[0]));
            return matcher.FindAsync(MediaKind.Movie, [fromFolder.Title, fromFile.Title], fromFolder.Year ?? fromFile.Year, ct);
        }

        var parsed = folder.Videos
            .Select(v => ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(v)))
            .OfType<ParsedEpisode>()
            .ToList();
        var fileShow = MostCommon(parsed.Select(p => p.ShowName).Where(s => s.Length > 0));
        var fileYear = MostCommon(parsed.Select(p => p.Year).Where(y => y.HasValue));

        return matcher.FindAsync(MediaKind.Series, [folderTitle, fileShow ?? ""], folderYear ?? fileYear, ct);
    }

    private static (string Title, int? Year) CleanName(string name)
    {
        var parsed = ReleaseNameParser.ParseMovie(name);
        return (parsed.Title.Length > 0 ? parsed.Title : name, parsed.Year);
    }

    private static T? MostCommon<T>(IEnumerable<T> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
}
