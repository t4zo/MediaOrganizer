using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma legenda escolhida na prévia para baixar.</summary>
/// <param name="Adjustment">Correção de sincronia medida na verificação, aplicada ao salvar (o cache continua com o original).</param>
public sealed record SubtitleDownload(FolderSubtitlePlan Folder, SubtitleQuery Query, SubtitleCandidate Candidate, SubtitleAdjustment? Adjustment = null)
{
    public string VideoPath => Query.VideoPath;
    public string TargetPath => SubtitleItem.TargetPath(Query.VideoPath, Candidate);

    /// <summary>Nota do journal: o idioma, e a correção se houver ("pt-BR (sincronia corrigida em +0,40 s)").</summary>
    internal string Note => Adjustment is { } adjustment ? $"{Candidate.Language} ({adjustment.Note})" : Candidate.Language.ToString();
}

/// <summary>Legendas externas de um vídeo MKV para embutir (botão "Embutir no vídeo").</summary>
public sealed record ExistingEmbed(FolderSubtitlePlan Folder, string VideoPath, IReadOnlyList<ExistingSubtitle> Subtitles);

/// <summary>
/// Baixa as legendas escolhidas e salva ao lado do vídeo (oculta por padrão) ou embute no MKV, registrando tudo
/// no journal <b>antes</b> de gravar, para o lote aparecer no Histórico e poder ser desfeito.
/// </summary>
public sealed class SubtitleDownloader(SubtitleProviderSet providers, HistoryStore history, ProcessedStore processed, SettingsStore settings,
    LibraryAnalyzer library, SubtitleEmbedder? embedder = null, SubtitleContentCache? contentCache = null)
{
    private const string TempSuffix = ".mo-tmp";

    private readonly SubtitleFetcher _fetcher = new(providers, contentCache);

    /// <summary>Uma unidade de trabalho: um arquivo de legenda, ou um vídeo que recebe faixas.</summary>
    private sealed record Unit(JournalOperation Operation, IReadOnlyList<SubtitleDownload> Downloads, string? EmbedVideo);

    /// <param name="output">
    /// <see cref="SubtitleOutput.Embed"/>: vídeos MKV recebem as legendas como faixas (uma passada por vídeo);
    /// os demais formatos ficam com o arquivo ao lado.
    /// </param>
    public async Task<ExecutionResult> ApplyAsync(string libraryPath, MediaKind kind, string description, IReadOnlyList<SubtitleDownload> downloads,
        SubtitleOutput output = SubtitleOutput.File, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var folders = downloads.Select(d => d.Folder).DistinctBy(f => f.Folder.Key).ToList();
        var unchangedBefore = folders.Where(WasUnchanged).Select(f => f.Folder.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var units = new List<Unit>();
        foreach (var group in downloads.GroupBy(d => d.VideoPath, StringComparer.OrdinalIgnoreCase))
        {
            if (output == SubtitleOutput.Embed && embedder is not null && MatroskaReader.IsMatroska(group.Key))
            {
                units.Add(new Unit(new JournalOperation
                {
                    Kind = OperationKind.Embed,
                    From = "",
                    To = group.Key,
                    Note = string.Join(", ", group.Select(d => d.Note)),
                }, [.. group], group.Key));
                continue;
            }

            units.AddRange(group.Select(d => new Unit(new JournalOperation
            {
                Kind = OperationKind.Create,
                From = $"{d.Candidate.Provider}:{d.Candidate.ProviderId}",
                To = d.TargetPath,
                Note = d.Note,
            }, [d], null)));
        }

        var journal = NewJournal(libraryPath, kind, description, folders, [.. units.Select(u => u.Operation)]);
        history.Save(journal);

        var current = settings.Current;
        var errors = new List<string>();
        var succeeded = 0;

        var batch = new BatchReporter(progress, units.Count);
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            batch.Start(index, unit.EmbedVideo ?? unit.Downloads[0].TargetPath, measurable: unit.EmbedVideo is not null);
            var op = unit.Operation;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (unit.EmbedVideo is { } video)
                {
                    await EmbedDownloadsAsync(video, unit.Downloads, op, current.KeepOriginalAfterEmbed, batch.Current(index, video), ct).ConfigureAwait(false);
                    succeeded += unit.Downloads.Count;
                }
                else
                {
                    var download = unit.Downloads[0];
                    var content = await FetchAsync(download, ct).ConfigureAwait(false);
                    Write(download.TargetPath, content);
                    if (current.HideSubtitleFiles) HiddenFile.TryHide(download.TargetPath);
                    succeeded++;
                }
                op.State = OperationState.Done;
            }
            catch (OperationCanceledException)
            {
                CancelFrom(journal, index, errors, op.To);
                break;
            }
            catch (Exception e) when (e is SubtitleProviderException or IOException or UnauthorizedAccessException)
            {
                if (e is SubtitleProviderException providerError) providers.Report(providerError);
                op.State = OperationState.Failed;
                op.Error = e.Message;
                errors.AddRange(unit.Downloads.Select(d => $"{Path.GetFileName(d.TargetPath)}: {e.Message}"));
            }

            history.Save(journal);
            batch.Done(index);
        }

        history.Save(journal);
        RecordFingerprints(folders, unchangedBefore);
        return new ExecutionResult(journal, succeeded, errors.Count, errors);
    }

    /// <summary>
    /// Embute legendas externas que já estão na pasta. As legendas usadas vão para ".mediaorganizer/originals"
    /// (operação Move no mesmo lote), então desfazer devolve tudo como estava.
    /// </summary>
    public async Task<ExecutionResult> EmbedExistingAsync(string libraryPath, MediaKind kind, string description, IReadOnlyList<ExistingEmbed> videos,
        IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var tool = embedder ?? throw new InvalidOperationException("Embutir legendas requer o mkvmerge.");
        var folders = videos.Select(v => v.Folder).DistinctBy(f => f.Folder.Key).ToList();
        var unchangedBefore = folders.Where(WasUnchanged).Select(f => f.Folder.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groups = videos
            .Select(v => (Video: v, Subs: v.Subtitles.Where(s => !s.Embedded && s.Path is not null && SubtitleText.IsText(Path.GetExtension(s.Path))).ToList()))
            .Where(g => g.Subs.Count > 0 && MatroskaReader.IsMatroska(g.Video.VideoPath))
            .ToList();

        var units = groups.Select(g => (
            Embed: new JournalOperation
            {
                Kind = OperationKind.Embed,
                From = "",
                To = g.Video.VideoPath,
                Note = string.Join(", ", g.Subs.Select(s => s.Language.ToString())),
            },
            Moves: g.Subs.Select(s => new JournalOperation
            {
                From = s.Path!,
                To = Path.Combine(SubtitleEmbedder.WorkDirectory(g.Video.VideoPath), "originals", Path.GetFileName(s.Path!)),
            }).ToList(),
            g.Subs)).ToList();

        var journal = NewJournal(libraryPath, kind, description, folders, [.. units.SelectMany(u => (IEnumerable<JournalOperation>)[u.Embed, .. u.Moves])]);
        history.Save(journal);

        var errors = new List<string>();
        var succeeded = 0;
        var batch = new BatchReporter(progress, units.Count);
        for (var index = 0; index < units.Count; index++)
        {
            var (embed, moves, subs) = units[index];
            batch.Start(index, embed.To, measurable: true);
            var temps = new List<string>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var tracks = new List<EmbedTrack>();
                foreach (var subtitle in subs)
                {
                    var temp = TempSubtitle(embed.To, subtitle.Language, Path.GetExtension(subtitle.Path!));
                    File.WriteAllBytes(temp, SubtitleText.ToUtf8(await File.ReadAllBytesAsync(subtitle.Path!, ct).ConfigureAwait(false)));
                    temps.Add(temp);
                    tracks.Add(new EmbedTrack(temp, subtitle.Language));
                }

                var result = await tool.EmbedAsync(embed.To, tracks, settings.Current.KeepOriginalAfterEmbed, batch.Current(index, embed.To), ct).ConfigureAwait(false);
                embed.From = result.BackupPath ?? "";
                embed.AddedTrackIds = result.AddedTrackIds;
                embed.State = OperationState.Done;
                succeeded++;

                foreach (var move in moves)
                {
                    try
                    {
                        RenameExecutor.Move(move.From, Unique(move.To), isDirectory: false);
                        move.State = OperationState.Done;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        move.State = OperationState.Failed;
                        move.Error = e.Message;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CancelFrom(journal, journal.Operations.IndexOf(embed), errors, embed.To);
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                embed.State = OperationState.Failed;
                embed.Error = e.Message;
                foreach (var move in moves)
                {
                    move.State = OperationState.Failed;
                    move.Error = "Não embutida.";
                }
                errors.Add($"{Path.GetFileName(embed.To)}: {e.Message}");
            }
            finally
            {
                foreach (var temp in temps) TryDelete(temp);
            }

            history.Save(journal);
            batch.Done(index);
        }

        history.Save(journal);
        RecordFingerprints(folders, unchangedBefore);
        return new ExecutionResult(journal, succeeded, errors.Count, errors);
    }

    /// <summary>Baixa todas as legendas do vídeo para a pasta de trabalho e embute numa só passada.</summary>
    private async Task EmbedDownloadsAsync(string video, IReadOnlyList<SubtitleDownload> downloads, JournalOperation op, bool keepOriginal,
        IProgress<double>? progress, CancellationToken ct)
    {
        var temps = new List<string>();
        try
        {
            var tracks = new List<EmbedTrack>();
            foreach (var download in downloads)
            {
                var content = await FetchAsync(download, ct).ConfigureAwait(false);
                var temp = TempSubtitle(video, download.Candidate.Language, "." + download.Candidate.Format);
                await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
                temps.Add(temp);
                tracks.Add(new EmbedTrack(temp, download.Candidate.Language));
            }

            var result = await embedder!.EmbedAsync(video, tracks, keepOriginal, progress, ct).ConfigureAwait(false);
            op.From = result.BackupPath ?? "";
            op.AddedTrackIds = result.AddedTrackIds;
        }
        finally
        {
            foreach (var temp in temps) TryDelete(temp);
        }
    }

    private static string TempSubtitle(string video, SubtitleLanguage language, string extension)
    {
        var dir = Path.Combine(SubtitleEmbedder.WorkDirectory(video), "tmp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{language.FileSuffix}{extension}");
    }

    private static RenameJournal NewJournal(string libraryPath, MediaKind kind, string description, List<FolderSubtitlePlan> folders,
        List<JournalOperation> operations) => new()
    {
        Id = RenameJournal.NewId(),
        CreatedAt = DateTimeOffset.Now,
        LibraryPath = libraryPath,
        Kind = kind,
        Description = description,
        FolderKeys = [.. folders.Select(f => f.Folder.Key)],
        Operations = operations,
    };

    private static void CancelFrom(RenameJournal journal, int index, List<string> errors, string current)
    {
        errors.Add($"{Path.GetFileName(current)}: cancelado");
        for (var rest = index; rest < journal.Operations.Count; rest++)
        {
            if (journal.Operations[rest].State != OperationState.Pending) continue;
            journal.Operations[rest].State = OperationState.Failed;
            journal.Operations[rest].Error = "Cancelado.";
        }
    }

    /// <summary>
    /// Baixa (ou reaproveita a da verificação de sincronia; falha sem rede se a cota do site já acabou), converte texto
    /// para UTF-8 e aplica a correção de sincronia escolhida.
    /// </summary>
    private async Task<byte[]> FetchAsync(SubtitleDownload download, CancellationToken ct)
    {
        var content = await _fetcher.FetchAsync(download.Candidate, download.Query, ct).ConfigureAwait(false);
        return download.Adjustment is { } adjustment && SubtitleText.IsText(download.Candidate.Format)
            ? SubtitleRetimer.Apply(content, adjustment)
            : content;
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

    private static string Unique(string path)
    {
        var result = path;
        for (var n = 2; File.Exists(result); n++)
            result = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{n}{Path.GetExtension(path)}");
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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

/// <summary>
/// Progresso de um lote: o geral (0 a 1) e o do arquivo atual (0 a 1, ou <c>null</c> quando não dá para medir,
/// como num download de legenda). <paramref name="Index"/> começa em 0.
/// </summary>
public readonly record struct BatchProgress(int Index, int Count, string CurrentPath, double Overall, double? Current);

/// <summary>Traduz o andamento de cada item (o progresso do mkvmerge) para o progresso do lote.</summary>
internal sealed class BatchReporter(IProgress<BatchProgress>? progress, int count)
{
    private string _path = "";

    /// <param name="measurable">O item tem progresso próprio (embutir): a barra dele começa em 0%.</param>
    public void Start(int index, string path, bool measurable)
    {
        _path = path;
        progress?.Report(new BatchProgress(index, count, path, index / (double)count, measurable ? 0 : null));
    }

    /// <summary>Progresso do item atual; repassado direto (sem <see cref="Progress{T}"/>, que trocaria a ordem das notificações).</summary>
    public IProgress<double>? Current(int index, string path) =>
        progress is null ? null : new Relay(p =>
        {
            var current = Math.Clamp(p, 0, 1);
            progress.Report(new BatchProgress(index, count, path, (index + current) / count, current));
        });

    public void Done(int index) => progress?.Report(new BatchProgress(index, count, _path, (index + 1) / (double)count, null));

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
