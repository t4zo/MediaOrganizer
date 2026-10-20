using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Execution;

namespace MediaOrganizer.App.ViewModels;

public sealed partial class HistoryViewModel(HistoryStore history, UndoService undo) : ObservableObject
{
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
            var text = $"{Journal.DoneCount} renomeado(s)";
            if (Journal.FailedCount > 0) text += $" · {Journal.FailedCount} com erro";
            if (Journal.UndoneAt is { } undone) text += $" · desfeito em {undone.LocalDateTime:dd/MM HH:mm}";
            return text;
        }
    }

    public bool CanUndo => Journal.CanUndo;

    public bool IsUndone => Journal.UndoneAt is not null;

    public IEnumerable<string> Operations => Journal.Operations
        .Take(200)
        .Select(o => $"{Path.GetFileName(o.From)}  →  {Path.GetFileName(o.To)}{(o.State == OperationState.Failed ? $"  (erro: {o.Error})" : "")}");

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private Task UndoAsync() => owner.UndoAsync(this);
}
