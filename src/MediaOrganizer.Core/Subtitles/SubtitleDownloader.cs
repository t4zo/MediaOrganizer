using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma legenda escolhida na prévia para baixar.</summary>
public sealed record SubtitleDownload(FolderSubtitlePlan Folder, SubtitleQuery Query, SubtitleCandidate Candidate)
{
    public string VideoPath => Query.VideoPath;
    public string TargetPath => SubtitleItem.TargetPath(Query.VideoPath, Candidate);
}

/// <summary>
/// Baixa as legendas escolhidas e salva ao lado do vídeo (oculta por padrão), registrando tudo no journal
/// <b>antes</b> de gravar, para o lote aparecer no Histórico e poder ser desfeito.
/// </summary>
public sealed class SubtitleDownloader(SubtitleProviderSet providers, HistoryStore history, ProcessedStore processed, SettingsStore settings,
    LibraryAnalyzer library)
{
    private const string TempSuffix = ".mo-tmp";

    public async Task<ExecutionResult> ApplyAsync(string libraryPath, MediaKind kind, string description, IReadOnlyList<SubtitleDownload> downloads,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var folders = downloads.Select(d => d.Folder).DistinctBy(f => f.Folder.Key).ToList();
        var unchangedBefore = folders.Where(WasUnchanged).Select(f => f.Folder.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var journal = new RenameJournal
        {
            Id = RenameJournal.NewId(),
            CreatedAt = DateTimeOffset.Now,
            LibraryPath = libraryPath,
            Kind = kind,
            Description = description,
            FolderKeys = [.. folders.Select(f => f.Folder.Key)],
            Operations =
            [
                .. downloads.Select(d => new JournalOperation
                {
                    Kind = OperationKind.Create,
                    From = $"{d.Candidate.Provider}:{d.Candidate.ProviderId}",
                    To = d.TargetPath,
                    Note = d.Candidate.Language.ToString(),
                }),
            ],
        };
        history.Save(journal);

        var hide = settings.Current.HideSubtitleFiles;
        var errors = new List<string>();
        var succeeded = 0;

        for (var index = 0; index < downloads.Count; index++)
        {
            var download = downloads[index];
            var op = journal.Operations[index];
            try
            {
                ct.ThrowIfCancellationRequested();
                var content = await FetchAsync(download, ct).ConfigureAwait(false);
                Write(download.TargetPath, content);
                if (hide) HiddenFile.TryHide(download.TargetPath);
                op.State = OperationState.Done;
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                op.State = OperationState.Failed;
                op.Error = "Cancelado.";
                errors.Add($"{Path.GetFileName(download.TargetPath)}: cancelado");
                for (var rest = index + 1; rest < downloads.Count; rest++)
                {
                    journal.Operations[rest].State = OperationState.Failed;
                    journal.Operations[rest].Error = "Cancelado.";
                }
                break;
            }
            catch (Exception e) when (e is SubtitleProviderException or IOException or UnauthorizedAccessException)
            {
                if (e is SubtitleProviderException providerError) providers.Report(providerError);
                op.State = OperationState.Failed;
                op.Error = e.Message;
                errors.Add($"{Path.GetFileName(download.TargetPath)}: {e.Message}");
            }

            history.Save(journal);
            progress?.Report((index + 1) / (double)downloads.Count);
        }

        history.Save(journal);
        RecordFingerprints(folders, unchangedBefore);
        return new ExecutionResult(journal, succeeded, errors.Count, errors);
    }

    /// <summary>Baixa (ou falha sem rede se a cota do site já acabou neste lote) e converte texto para UTF-8.</summary>
    private async Task<byte[]> FetchAsync(SubtitleDownload download, CancellationToken ct)
    {
        var provider = providers.Find(download.Candidate.Provider)
                       ?? throw new IOException($"Site {download.Candidate.Provider} indisponível.");
        if (providers.DisabledReason(provider) is { } reason)
            throw new SubtitleProviderException(provider.Name, SubtitleProviderError.QuotaExceeded, reason);

        var file = await provider.DownloadAsync(download.Candidate, download.Query, ct).ConfigureAwait(false);
        if (file.Content.Length == 0)
            throw new SubtitleProviderException(provider.Name, SubtitleProviderError.InvalidContent, "O site devolveu um arquivo vazio.");
        return SubtitleText.IsText(file.Extension) ? SubtitleText.ToUtf8(file.Content) : file.Content;
    }

    /// <summary>Grava num temporário e move para o destino sem sobrescrever.</summary>
    internal static void Write(string target, byte[] content)
    {
        if (File.Exists(target)) throw new IOException("Já existe um arquivo com este nome.");
        var temp = target + TempSuffix;
        File.WriteAllBytes(temp, content);
        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>A pasta estava registrada como "nada foi alterado"? Então continua assim depois das legendas novas.</summary>
    private bool WasUnchanged(FolderSubtitlePlan plan) =>
        !plan.Folder.IsLooseFile && processed.IsUnchanged(Rescan(plan.Folder));

    private void RecordFingerprints(List<FolderSubtitlePlan> folders, HashSet<string> unchangedBefore)
    {
        foreach (var plan in folders)
        {
            if (!unchangedBefore.Contains(plan.Folder.Key) || processed.Get(plan.Folder.Key) is not { } entry) continue;
            processed.MarkProcessed(Rescan(plan.Folder), entry.TmdbId);
        }
    }

    private LibraryFolder Rescan(LibraryFolder folder) =>
        library.CreateScanner().ScanFolder(folder.Kind, folder.RootPath, folder.Path);
}
