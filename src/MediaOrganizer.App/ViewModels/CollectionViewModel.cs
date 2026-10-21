using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Collection;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.App.ViewModels;

public enum CollectionFilter
{
    All,
    Incomplete,
    Complete,
    NoMatch,
}

/// <summary>Tela Coleção: episódios faltando por série e temporada.</summary>
public sealed partial class CollectionViewModel : ObservableObject
{
    private readonly ShellServices _shell;
    private readonly SettingsStore _settings;
    private readonly LibraryAnalyzer _library;
    private readonly CollectionAnalyzer _analyzer;
    private readonly MediaMatcher _matcher;
    private readonly PosterLoader _posters;

    public CollectionViewModel(ShellServices shell, SettingsStore settings, LibraryAnalyzer library, CollectionAnalyzer analyzer,
        MediaMatcher matcher, PosterLoader posters)
    {
        _shell = shell;
        _settings = settings;
        _library = library;
        _analyzer = analyzer;
        _matcher = matcher;
        _posters = posters;
        LibraryPath = settings.Current.LastKind == MediaKind.Series ? settings.Current.LastLibraryPath ?? "" : "";
    }

    public ObservableCollection<SeriesReportViewModel> Series { get; } = [];
    public ObservableCollection<SeriesReportViewModel> VisibleSeries { get; } = [];

    public IReadOnlyList<string> FilterOptions { get; } = ["Todas as séries", "Incompletas", "Completas", "Sem correspondência"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(RefreshAllCommand))]
    public partial string LibraryPath { get; set; }

    [ObservableProperty]
    public partial SeriesReportViewModel? SelectedSeries { get; set; }

    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand), nameof(ExportTextCommand))]
    public partial bool HasResults { get; private set; }

    public int CompleteCount => Series.Count(s => s.IsComplete);
    public int IncompleteCount => Series.Count(s => s.IsIncomplete);
    public int ProblemCount => Series.Count(s => s.IsProblem);
    public int TotalMissing => Series.Sum(s => s.Report.MissingCount);
    public bool HasIncomplete => IncompleteCount > 0;
    public bool HasProblems => ProblemCount > 0;

    public string Summary => TotalMissing == 0
        ? "Nenhum episódio faltando nas séries identificadas."
        : $"{TotalMissing} episódio(s) faltando em {IncompleteCount} série(s)";

    partial void OnLibraryPathChanged(string value) => Clear();

    partial void OnFilterIndexChanged(int value) => RefreshVisible();

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _shell.PickFolderAsync("Escolha a pasta de séries", LibraryPath) is { } path) LibraryPath = path;
    }

    private bool CanAnalyze() => Directory.Exists(LibraryPath);

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private Task AnalyzeAsync(CancellationToken ct) => RunAsync(refresh: false, ct);

    /// <summary>Busca de novo no TMDb, ignorando o cache (novos episódios de séries em exibição).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task RefreshAllAsync() => RunAsync(refresh: true, CancellationToken.None);

    private async Task RunAsync(bool refresh, CancellationToken ct)
    {
        var previous = SelectedSeries?.FolderName;
        Clear();
        IsAnalyzing = true;
        Progress = 0;
        ProgressText = "Lendo a pasta...";

        try
        {
            var root = LibraryPath;
            var folders = await Task.Run(() => _library.Scan(root, MediaKind.Series), ct);
            if (folders.Count == 0)
            {
                _shell.ShowToast("Nenhuma série encontrada. Cada série deve estar em uma subpasta da pasta escolhida.");
                return;
            }

            var done = 0;
            await foreach (var report in _analyzer.AnalyzeAsync(folders, refresh, ct))
            {
                var vm = new SeriesReportViewModel(report, this);
                Insert(vm);
                _ = vm.LoadPosterAsync(_posters);
                done++;
                Progress = done * 100.0 / folders.Count;
                ProgressText = $"Verificando {done} de {folders.Count} — {report.Folder.Name}";
            }

            HasResults = true;
            SelectedSeries = VisibleSeries.FirstOrDefault(s => s.FolderName == previous)
                             ?? VisibleSeries.FirstOrDefault(s => s.IsIncomplete)
                             ?? VisibleSeries.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            HasResults = Series.Count > 0;
            _shell.ShowToast("Verificação cancelada.");
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

    private bool CanExport() => HasResults;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsvAsync() => ExportAsync(CollectionExporter.ToCsv(Reports()), "CSV", "*.csv");

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportTextAsync() => ExportAsync(CollectionExporter.ToText(Reports()), "Texto", "*.txt");

    private async Task ExportAsync(string content, string typeName, string pattern)
    {
        var name = $"episodios-faltando-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{Path.GetExtension(pattern)}";
        try
        {
            if (await _shell.SaveTextFileAsync("Exportar episódios faltando", name, content, typeName, pattern) is { } saved)
                _shell.ShowToast($"Lista exportada para {Path.GetFileName(saved)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast($"Não foi possível salvar: {e.Message}", isError: true);
        }
    }

    internal async Task IdentifyAsync(SeriesReportViewModel series)
    {
        var dialog = new MatchDialogViewModel(series.Report.Folder, _matcher, _posters, series.Report.Show);
        if (await _shell.ShowDialogAsync(dialog) is not { } chosen) return;

        _settings.SetMatch(series.Report.Folder.MatchKey, chosen.Id);
        await ReanalyzeAsync(series, refresh: false);
    }

    internal async Task ReanalyzeAsync(SeriesReportViewModel series, bool refresh)
    {
        series.IsBusy = true;
        try
        {
            var folder = _library.CreateScanner().ScanFolder(MediaKind.Series, series.Report.Folder.RootPath, series.Report.Folder.Path);
            series.Replace(await _analyzer.AnalyzeSeriesAsync(folder, refresh, CancellationToken.None));
            await series.LoadPosterAsync(_posters);
        }
        catch (TmdbAuthenticationException e)
        {
            _shell.ShowToast(e.Message, isError: true);
        }
        finally
        {
            series.IsBusy = false;
            NotifyTotals();
            RefreshVisible();
        }
    }

    internal Task OpenUrlAsync(string url) => _shell.OpenUrlAsync(url);

    private IEnumerable<SeriesReport> Reports() => Series.Select(s => s.Report);

    private void Insert(SeriesReportViewModel series)
    {
        var index = 0;
        while (index < Series.Count && CompareNames(Series[index].Name, series.Name) < 0) index++;
        Series.Insert(index, series);
        if (Matches(series)) VisibleSeries.Insert(VisibleSeries.TakeWhile(s => CompareNames(s.Name, series.Name) < 0).Count(), series);
        NotifyTotals();
    }

    private static int CompareNames(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase);

    private void RefreshVisible()
    {
        var selected = SelectedSeries;
        VisibleSeries.Clear();
        foreach (var series in Series.Where(Matches)) VisibleSeries.Add(series);
        SelectedSeries = selected is not null && VisibleSeries.Contains(selected) ? selected : VisibleSeries.FirstOrDefault();
    }

    private bool Matches(SeriesReportViewModel series)
    {
        var byStatus = (CollectionFilter)FilterIndex switch
        {
            CollectionFilter.Incomplete => series.IsIncomplete,
            CollectionFilter.Complete => series.IsComplete,
            CollectionFilter.NoMatch => series.IsProblem,
            _ => true,
        };
        return byStatus && (SearchText.Length == 0 ||
                            series.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            series.FolderName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Clear()
    {
        Series.Clear();
        VisibleSeries.Clear();
        SelectedSeries = null;
        HasResults = false;
        NotifyTotals();
    }

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(CompleteCount));
        OnPropertyChanged(nameof(IncompleteCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(TotalMissing));
        OnPropertyChanged(nameof(HasIncomplete));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Summary));
    }
}
