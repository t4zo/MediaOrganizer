using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.App.ViewModels;

public sealed partial class HistoryViewModel(HistoryStore history, UndoService undo, ShellServices shell, SubtitleEmbedder embedder) : ObservableObject
{
    /// <summary>Manda os vídeos originais guardados ao embutir legendas para a Lixeira; desfazer passa a usar o remux.</summary>
    internal async Task RemoveBackupsAsync(BatchViewModel batch)
    {
        if (!await shell.ConfirmAsync("Remover os vídeos originais guardados?",
                $"{batch.BackupText} serão enviados para a Lixeira. Em HD externo sem Lixeira, a exclusão é definitiva. " +
                "Depois disso, desfazer este lote remove as faixas com um novo processamento (requer o mkvmerge).",
                "Remover", destructive: true))
            return;

        try
        {
            await Task.Run(() => embedder.RemoveBackups(batch.Journal));
            shell.ShowToast("Backups removidos.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            shell.ShowToast($"Não foi possível remover: {e.Message}", isError: true);
        }
        finally
        {
            history.Save(batch.Journal);
            Refreshed();
        }
    }

    public ObservableCollection<BatchViewModel> Batches { get; } = [];

    public bool IsEmpty => Batches.Count == 0;

    public HistoryViewModel Refreshed()
    {
        Batches.Clear();
        foreach (var journal in history.List()) Batches.Add(new BatchViewModel(journal, this));
        OnPropertyChanged(nameof(IsEmpty));
        return this;
    }

    internal async Task UndoAsync(BatchViewModel batch)
    {
        if (await undo.UndoAsync(batch.Journal)) Refreshed();
    }
}

public sealed partial class BatchViewModel(RenameJournal journal, HistoryViewModel owner) : ObservableObject
{
    public RenameJournal Journal { get; } = journal;

    public string Title => Journal.Description;

    public string When => Journal.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string KindText => Journal.Kind == MediaKind.Series ? "Séries" : "Filmes";

    public string Summary
    {
        get
        {
            var done = Journal.Operations.Where(o => o.State is OperationState.Done or OperationState.Undone).ToList();
            var parts = new List<string>();
            var moved = done.Count(o => o.Kind == OperationKind.Move);
            var created = done.Count(o => o.Kind == OperationKind.Create);
            var embedded = done.Count(o => o.Kind == OperationKind.Embed);
            if (moved > 0 || done.Count == 0) parts.Add($"{moved} renomeado(s)");
            if (created > 0) parts.Add($"{created} legenda(s) baixada(s)");
            if (embedded > 0) parts.Add($"{embedded} vídeo(s) com legendas embutidas");
            var text = string.Join(" · ", parts);
            if (Journal.FailedCount > 0) text += $" · {Journal.FailedCount} com erro";
            if (Journal.UndoneAt is { } undone) text += $" · desfeito em {undone.LocalDateTime:dd/MM HH:mm}";
            return text;
        }
    }

    public bool CanUndo => Journal.CanUndo;

    public bool IsUndone => Journal.UndoneAt is not null;

    public IEnumerable<string> Operations => Journal.Operations
        .Take(200)
        .Select(o => Describe(o) + (o.State == OperationState.Failed ? $"  (erro: {o.Error})" : ""));

    private static string Describe(JournalOperation operation) => operation.Kind switch
    {
        OperationKind.Create => $"Baixada: {Path.GetFileName(operation.To)}",
        OperationKind.Embed => $"Embutida: {operation.Note} em {Path.GetFileName(operation.To)}",
        _ => $"{Path.GetFileName(operation.From)}  →  {Path.GetFileName(operation.To)}",
    };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private Task UndoAsync() => owner.UndoAsync(this);

    private long? _backupSize;

    /// <summary>Espaço ocupado pelos vídeos originais guardados ao embutir legendas.</summary>
    public long BackupSize => _backupSize ??= Journal.UndoneAt is null ? SubtitleEmbedder.BackupSize(Journal) : 0;

    public bool HasBackups => BackupSize > 0;

    public string BackupText => string.Create(System.Globalization.CultureInfo.CurrentCulture,
        $"Originais guardados: {BackupSize / (1024.0 * 1024 * 1024):0.00} GB");

    [RelayCommand]
    private Task RemoveBackupsAsync() => owner.RemoveBackupsAsync(this);
}
