using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.App.ViewModels;

public enum FolderFilter
{
    All,
    Changes,
    Review,
    Done,
}

/// <summary>Tela principal: analisa a biblioteca, mostra a prévia e aplica as renomeações.</summary>
public sealed partial class OrganizeViewModel : ObservableObject
{
    private readonly ShellServices _shell;
    private readonly SettingsStore _settings;
    private readonly LibraryAnalyzer _analyzer;
    private readonly RenameExecutor _executor;
    private readonly HistoryStore _history;
    private readonly UndoService _undo;
    private readonly MediaMatcher _matcher;
    private readonly PosterLoader _posters;

    public OrganizeViewModel(ShellServices shell, SettingsStore settings, LibraryAnalyzer analyzer, RenameExecutor executor,
        HistoryStore history, UndoService undo, MediaMatcher matcher, PosterLoader posters)
    {
        _shell = shell;
        _settings = settings;
        _analyzer = analyzer;
        _executor = executor;
        _history = history;
        _undo = undo;
        _matcher = matcher;
        _posters = posters;

        LibraryPath = settings.Current.LastLibraryPath ?? "";
        IsMovies = settings.Current.LastKind == MediaKind.Movie;
        RenameSubtitles = settings.Current.RenameSubtitles;

        undo.Undone += _ =>
        {
            if (HasResults && !IsAnalyzing) AnalyzeCommand.Execute(null);
        };
    }

    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    public ObservableCollection<FolderViewModel> VisibleFolders { get; } = [];

    public IReadOnlyList<string> FilterOptions { get; } = ["Todas as pastas", "Com alterações", "Para revisar", "Concluídas"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial string LibraryPath { get; set; }

    [ObservableProperty]
    public partial bool IsMovies { get; set; }

    [ObservableProperty]
    public partial bool RenameSubtitles { get; set; }

    [ObservableProperty]
    public partial FolderViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowUnchangedFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasResults { get; private set; }

    public bool IsBusy => IsAnalyzing || IsApplying;

    /// <summary>0 = Séries, 1 = Filmes (abas do topo).</summary>
    public int KindIndex
    {
        get => IsMovies ? 1 : 0;
        set => IsMovies = value == 1;
    }

    public int SelectedFileCount => Folders.Where(f => !f.LowConfidence).Sum(f => f.SelectedCount);
    public int SelectedFolderCount => Folders.Count(f => !f.LowConfidence && f.SelectedCount > 0);
    public int ReviewCount => Folders.Count(f => f.IsReview);
    public int ChangesCount => Folders.Count(f => f.IsChanges);
    public int DoneCount => Folders.Count(f => f.IsDone);
    public int ProblemCount => Folders.Count(f => f.IsProblem);
    public bool HasReview => ReviewCount > 0;
    public bool HasProblems => ProblemCount > 0;

    /// <summary>Todas as pastas foram processadas antes e nada mudou: mostra o aviso "nada foi alterado".</summary>
    public bool NothingChanged => HasResults && !IsAnalyzing && Folders.Count > 0 &&
                                  Folders.All(f => f.Status == FolderStatus.AlreadyProcessed);

    public bool AllInStandard => HasResults && !IsAnalyzing && !NothingChanged && Folders.Count > 0 && Folders.All(f => f.IsDone);

    public string Summary => SelectedFileCount == 0
        ? "Nenhum arquivo selecionado"
        : $"{SelectedFileCount} arquivo(s) em {SelectedFolderCount} pasta(s) serão renomeados";

    partial void OnIsMoviesChanged(bool value)
    {
        OnPropertyChanged(nameof(KindIndex));
        ClearResults();
    }

    partial void OnLibraryPathChanged(string value) => ClearResults();

    partial void OnFilterIndexChanged(int value) => RefreshVisibleFolders();

    partial void OnSearchTextChanged(string value) => RefreshVisibleFolders();

    partial void OnShowUnchangedFilesChanged(bool value)
    {
        foreach (var folder in Folders) folder.ApplyFilter();
    }

    partial void OnRenameSubtitlesChanged(bool value) => _settings.Update(s => s.RenameSubtitles = value);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _shell.PickFolderAsync("Escolha a pasta da biblioteca", LibraryPath) is { } path) LibraryPath = path;
    }

    private bool CanAnalyze() => Directory.Exists(LibraryPath);

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private Task AnalyzeAsync(CancellationToken ct) => RunAnalysisAsync(force: false, ct);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task ForceAnalyzeAsync() => RunAnalysisAsync(force: true, CancellationToken.None);

    private async Task RunAnalysisAsync(bool force, CancellationToken ct)
    {
        var root = LibraryPath;
        var kind = IsMovies ? MediaKind.Movie : MediaKind.Series;
        _settings.Update(s =>
        {
            s.LastLibraryPath = root;
            s.LastKind = kind;
        });

        var previousSelection = SelectedFolder?.Name;
        ClearResults();
        IsAnalyzing = true;
        Progress = 0;
        ProgressText = "Lendo a pasta...";

        try
        {
            var folders = await Task.Run(() => _analyzer.Scan(root, kind), ct);
            if (folders.Count == 0)
            {
                _shell.ShowToast(kind == MediaKind.Series
                    ? "Nenhuma série encontrada. Cada série deve estar em uma subpasta da pasta escolhida."
                    : "Nenhum filme encontrado nesta pasta.");
                return;
            }

            var done = 0;
            await foreach (var plan in _analyzer.AnalyzeAsync(folders, new AnalyzeOptions(RenameSubtitles, force), ct))
            {
                var folder = new FolderViewModel(plan, this);
                InsertSorted(folder);
                _ = folder.LoadPosterAsync(_posters);

                done++;
                Progress = done * 100.0 / folders.Count;
                ProgressText = $"Analisando {done} de {folders.Count} — {plan.Folder.Name}";
            }

            HasResults = true;
            SelectedFolder = VisibleFolders.FirstOrDefault(f => f.Name == previousSelection)
                             ?? VisibleFolders.FirstOrDefault(f => f.IsReview || f.IsChanges)
                             ?? VisibleFolders.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            HasResults = Folders.Count > 0;
            _shell.ShowToast("Análise cancelada.");
        }
        catch (TmdbAuthenticationException e)
        {
            _shell.ShowToast(e.Message, isError: true, "Abrir configurações", () =>
            {
                _shell.Navigate(Page.Settings);
                return Task.CompletedTask;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast($"Não foi possível ler a pasta: {e.Message}", isError: true);
        }
        finally
        {
            IsAnalyzing = false;
            NotifyTotals();
        }
    }

    private bool CanApply() => !IsApplying && SelectedFileCount > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var folders = Folders.Where(f => !f.LowConfidence && f.SelectedCount > 0).ToList();
        var items = folders.SelectMany(f => f.SelectedItems).ToList();
        if (items.Count == 0) return;

        if (!await _shell.ConfirmAsync("Aplicar renomeações?",
                $"{items.Count} arquivo(s) em {folders.Count} pasta(s) serão renomeados.\nVocê pode desfazer depois pelo botão \"Desfazer\" ou pelo Histórico.",
                "Renomear"))
            return;

        IsApplying = true;
        ProgressText = "Renomeando...";
        Progress = 0;
        try
        {
            var root = LibraryPath;
            var kind = IsMovies ? MediaKind.Movie : MediaKind.Series;
            var description = folders.Count == 1
                ? $"{folders[0].MatchTitle ?? folders[0].Name}: {items.Count} arquivo(s)"
                : $"{items.Count} arquivo(s) em {folders.Count} pastas";
            var progress = new Progress<double>(p => Progress = p * 100);

            var result = await Task.Run(() => _executor.Apply(root, kind, description, items, folders.Select(f => f.Plan.Folder.Key), progress));

            // Registra fingerprint/correspondência das pastas totalmente aplicadas.
            var done = result.Journal.Operations.Where(o => o.State == OperationState.Done).Select(o => o.From)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                var applied = folder.SelectedItems.Where(i => done.Contains(i.SourcePath)).ToList();
                if (_analyzer.RecordApplied(folder.EditedPlan, applied) is { } key && !result.Journal.FolderKeys.Contains(key))
                    result.Journal.FolderKeys.Add(key);
            }
            _history.Save(result.Journal);

            if (result.Failed == 0)
                _shell.ShowToast($"{result.Succeeded} arquivo(s) renomeado(s).", actionText: "Desfazer",
                    action: () => _undo.UndoAsync(result.Journal, confirm: false));
            else
                _shell.ShowToast($"{result.Succeeded} renomeado(s), {result.Failed} com erro. Primeiro erro: {result.Errors[0]}", isError: true,
                    actionText: "Desfazer", action: () => _undo.UndoAsync(result.Journal, confirm: false));
        }
        finally
        {
            IsApplying = false;
        }

        await RunAnalysisAsync(force: false, CancellationToken.None);
    }

    internal async Task ChangeMatchAsync(FolderViewModel folder)
    {
        var dialog = new MatchDialogViewModel(folder.Plan.Folder, _matcher, _posters, folder.Plan.Match);
        if (await _shell.ShowDialogAsync(dialog) is not { } chosen) return;
        await RunFolderAsync(folder, chosen, force: true);
    }

    internal Task ReanalyzeAsync(FolderViewModel folder, bool force) => RunFolderAsync(folder, null, force);

    internal void ConfirmMatch(FolderViewModel folder)
    {
        if (folder.Plan.Match is not { } match) return;
        _settings.SetMatch(folder.Plan.Folder.MatchKey, match.Id);
        var items = folder.EditedPlan.Items;
        folder.Replace(folder.Plan with { LowConfidence = false, Items = items, Status = FolderPlan.StatusFor(items, false) });
        OnSelectionChanged();
        RefreshVisibleFolders();
    }

    internal async Task TrashJunkAsync(FolderViewModel folder)
    {
        var selected = folder.Junk.Where(j => j.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (!await _shell.ConfirmAsync("Mover para a Lixeira?",
                $"{selected.Count} item(ns) de \"{folder.Name}\" serão movidos para a Lixeira do sistema. Você pode restaurá-los de lá.",
                "Mover para a Lixeira", destructive: true))
            return;

        var removed = new List<JunkItemViewModel>();
        var errors = new List<string>();
        await Task.Run(() =>
        {
            // Pastas por último e das mais profundas para as mais rasas.
            foreach (var junk in selected.OrderBy(j => j.IsDirectory).ThenByDescending(j => j.Path.Length))
            {
                try
                {
                    RecycleBin.Send(junk.Path);
                    removed.Add(junk);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{junk.Display}: {e.Message}");
                }
            }
        });

        folder.RemoveJunk(removed);
        if (errors.Count == 0) _shell.ShowToast($"{removed.Count} item(ns) movido(s) para a Lixeira.");
        else _shell.ShowToast($"{errors.Count} item(ns) não puderam ser movidos: {errors[0]}", isError: true);
    }

    internal Task RevealAsync(string path) => _shell.RevealFolderAsync(path);

    internal Task OpenUrlAsync(string url) => _shell.OpenUrlAsync(url);

    internal void OnSelectionChanged()
    {
        NotifyTotals();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task RunFolderAsync(FolderViewModel folder, MediaCandidate? chosen, bool force)
    {
        folder.IsBusy = true;
        try
        {
            var options = new AnalyzeOptions(RenameSubtitles, force);
            var current = _analyzer.CreateScanner().ScanFolder(folder.Plan.Folder.Kind, folder.Plan.Folder.RootPath, folder.Plan.Folder.Path);
            var scanned = folder.Plan.Folder.IsLooseFile ? folder.Plan.Folder : current;
            folder.Replace(await _analyzer.AnalyzeFolderAsync(scanned, options, chosen, CancellationToken.None));
            await folder.LoadPosterAsync(_posters);
        }
        catch (TmdbAuthenticationException e)
        {
            _shell.ShowToast(e.Message, isError: true);
        }
        finally
        {
            folder.IsBusy = false;
            OnSelectionChanged();
            RefreshVisibleFolders();
        }
    }

    private void InsertSorted(FolderViewModel folder)
    {
        var index = 0;
        while (index < Folders.Count && CompareNames(Folders[index].Name, folder.Name) < 0) index++;
        Folders.Insert(index, folder);
        if (Matches(folder))
        {
            var visibleIndex = VisibleFolders.TakeWhile(f => CompareNames(f.Name, folder.Name) < 0).Count();
            VisibleFolders.Insert(visibleIndex, folder);
        }
        NotifyTotals();
    }

    private static int CompareNames(string a, string b) =>
        System.Globalization.CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.IgnoreCase);

    private void RefreshVisibleFolders()
    {
        var selected = SelectedFolder;
        VisibleFolders.Clear();
        foreach (var folder in Folders.Where(Matches)) VisibleFolders.Add(folder);
        SelectedFolder = selected is not null && VisibleFolders.Contains(selected) ? selected : VisibleFolders.FirstOrDefault();
    }

    private bool Matches(FolderViewModel folder)
    {
        var byStatus = (FolderFilter)FilterIndex switch
        {
            FolderFilter.Changes => folder.IsChanges,
            FolderFilter.Review => folder.IsReview || folder.IsProblem,
            FolderFilter.Done => folder.IsDone,
            _ => true,
        };
        return byStatus && (SearchText.Length == 0 ||
                            folder.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            (folder.MatchTitle?.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    private void ClearResults()
    {
        Folders.Clear();
        VisibleFolders.Clear();
        SelectedFolder = null;
        HasResults = false;
        NotifyTotals();
    }

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedFolderCount));
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(ChangesCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(NothingChanged));
        OnPropertyChanged(nameof(AllInStandard));
        OnPropertyChanged(nameof(Summary));
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
