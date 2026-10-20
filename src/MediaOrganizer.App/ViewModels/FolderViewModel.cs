using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.Core.Planning;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Uma série/filme na lista da esquerda, com seus itens de prévia.</summary>
public sealed partial class FolderViewModel : ObservableObject
{
    private readonly OrganizeViewModel _owner;
    private bool _suspendRefresh;

    public FolderViewModel(FolderPlan plan, OrganizeViewModel owner)
    {
        _owner = owner;
        Plan = plan;
        Load(plan);
    }

    public FolderPlan Plan { get; private set; }

    public ObservableCollection<ItemViewModel> Items { get; } = [];
    public ObservableCollection<ItemViewModel> VisibleItems { get; } = [];
    public ObservableCollection<JunkItemViewModel> Junk { get; } = [];

    public string Name => Plan.Folder.Name;
    public string Path => Plan.Folder.IsLooseFile ? Plan.Folder.Videos[0] : Plan.Folder.Path;

    public string? MatchTitle => Plan.Match?.DisplayName;
    public string? MatchOriginal => Plan.Match is { } m && m.OriginalTitle != m.PreferredTitle ? m.OriginalTitle : null;
    public string? MatchOverview => Plan.Match?.Overview;
    public bool HasMatch => Plan.Match is not null;
    public bool LowConfidence => Plan.LowConfidence;
    public string? Message => Plan.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Plan.Message);
    public bool HasJunk => Junk.Count > 0;

    [ObservableProperty]
    public partial Bitmap? Poster { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public FolderStatus Status => Plan.Status;
    public bool IsChanges => Status == FolderStatus.Changes;
    public bool IsReview => Status == FolderStatus.NeedsReview;
    public bool IsDone => Status is FolderStatus.NoChanges or FolderStatus.AlreadyProcessed;
    public bool IsProblem => Status is FolderStatus.NotFound or FolderStatus.Error;
    public bool CanChangeMatch => Status != FolderStatus.AlreadyProcessed || HasMatch;

    public string StatusText => Status switch
    {
        FolderStatus.Changes => $"{SelectedCount} de {ChangeCount} para renomear",
        FolderStatus.NoChanges => "Tudo no padrão",
        FolderStatus.AlreadyProcessed => "Nada alterado desde o último processamento",
        FolderStatus.NeedsReview => LowConfidence ? "Confirme a correspondência" : "Há conflitos para revisar",
        FolderStatus.NotFound => "Não encontrado no TMDb",
        FolderStatus.Error => "Erro na análise",
        _ => "",
    };

    public int ChangeCount => Items.Count(i => i.IsChange);
    public bool HasChanges => ChangeCount > 0;
    public bool HasVisibleItems => VisibleItems.Count > 0;
    public int SelectedCount => Items.Count(i => i.IsSelected && i.IsChange);

    public IEnumerable<RenameItem> SelectedItems => Items.Where(i => i.IsSelected && i.IsChange).Select(i => i.Item);

    /// <summary>Plano atualizado com as edições feitas pelo usuário.</summary>
    public FolderPlan EditedPlan => Plan with { Items = [.. Items.Select(i => i.Item)] };

    public void Replace(FolderPlan plan)
    {
        Plan = plan;
        Load(plan);
        OnPropertyChanged(string.Empty);
    }

    internal void ApplyFilter()
    {
        VisibleItems.Clear();
        foreach (var item in Items.Where(i => _owner.ShowUnchangedFiles || !i.IsUnchanged)) VisibleItems.Add(item);
        OnPropertyChanged(nameof(HasVisibleItems));
    }

    internal async Task LoadPosterAsync(Services.PosterLoader loader) => Poster = await loader.LoadAsync(Plan.Match?.PosterUrl);

    [RelayCommand]
    private Task ChangeMatchAsync() => _owner.ChangeMatchAsync(this);

    [RelayCommand]
    private Task ReanalyzeAsync() => _owner.ReanalyzeAsync(this, force: true);

    [RelayCommand]
    private void ConfirmMatch() => _owner.ConfirmMatch(this);

    [RelayCommand]
    private Task TrashJunkAsync() => _owner.TrashJunkAsync(this);

    [RelayCommand]
    private Task OpenFolderAsync() => _owner.RevealAsync(Plan.Folder.IsLooseFile ? Plan.Folder.RootPath : Plan.Folder.Path);

    [RelayCommand]
    private Task OpenTmdbAsync() => Plan.Match is { } m ? _owner.OpenUrlAsync(m.WebUrl) : Task.CompletedTask;

    [RelayCommand]
    private void SelectAll(bool select)
    {
        _suspendRefresh = true;
        foreach (var item in Items.Where(i => i.CanSelect)) item.IsSelected = select;
        _suspendRefresh = false;
        OnItemsChanged();
    }

    internal void RemoveJunk(IEnumerable<JunkItemViewModel> removed)
    {
        foreach (var junk in removed.ToList()) Junk.Remove(junk);
        OnPropertyChanged(nameof(HasJunk));
    }

    private void Load(FolderPlan plan)
    {
        _suspendRefresh = true;
        var folderPath = plan.Folder.IsLooseFile ? plan.Folder.RootPath : plan.Folder.Path;

        Items.Clear();
        // Ordem natural: por episódio (nome de destino), vídeo antes das legendas; renomeação de pasta por último.
        foreach (var item in plan.Items
                     .OrderBy(i => i.Kind == ItemKind.Folder)
                     .ThenBy(i => SortKey(i), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Kind))
        {
            Items.Add(new ItemViewModel(item, folderPath, OnItemsChanged));
        }

        Junk.Clear();
        foreach (var file in plan.Folder.Junk) Junk.Add(new JunkItemViewModel(file, false, folderPath));
        foreach (var dir in plan.Folder.EmptyDirectories) Junk.Add(new JunkItemViewModel(dir, true, folderPath));

        _suspendRefresh = false;
        ApplyFilter();
    }

    /// <summary>Nome de destino sem extensão e sem sufixos de legenda (".en", ".forced", ".2").</summary>
    private static string SortKey(RenameItem item)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(item.TargetPath);
        if (item.Kind != ItemKind.Subtitle) return name;
        for (var i = 0; i < 3; i++)
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || name.Length - dot > 8) break;
            name = name[..dot];
        }
        return name;
    }

    /// <summary>Uma edição/seleção mudou: refaz a checagem de conflitos e os totais.</summary>
    private void OnItemsChanged()
    {
        if (_suspendRefresh) return;
        _suspendRefresh = true;

        var checkedItems = ConflictDetector.Apply([.. Items.Select(i => i.Item)]);
        for (var i = 0; i < Items.Count; i++)
        {
            if (!ReferenceEquals(Items[i].Item, checkedItems[i]) && Items[i].Item != checkedItems[i]) Items[i].Update(checkedItems[i]);
        }

        Plan = Plan with
        {
            Items = checkedItems,
            Status = Plan.Status is FolderStatus.Changes or FolderStatus.NeedsReview or FolderStatus.NoChanges
                ? FolderPlan.StatusFor(checkedItems, Plan.LowConfidence)
                : Plan.Status,
        };

        _suspendRefresh = false;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsChanges));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsDone));
        _owner.OnSelectionChanged();
    }
}
