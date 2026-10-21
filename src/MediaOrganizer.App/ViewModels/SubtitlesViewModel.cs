using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.App.ViewModels;

public enum SubtitleFilter
{
    All,
    Missing,
    Complete,
    NoMatch,
}

/// <summary>Tela Legendas: busca as legendas que faltam e baixa as selecionadas, com prévia e desfazer.</summary>
public sealed partial class SubtitlesViewModel : ObservableObject
{
    private readonly ShellServices _shell;
    private readonly SettingsStore _settings;
    private readonly LibraryAnalyzer _library;
    private readonly SubtitleAnalyzer _analyzer;
    private readonly SubtitleDownloader _downloader;
    private readonly SubtitleProviderSet _providers;
    private readonly MediaMatcher _matcher;
    private readonly PosterLoader _posters;
    private readonly UndoService _undo;
    private readonly SubtitleEmbedder _embedder;

    public SubtitlesViewModel(ShellServices shell, SettingsStore settings, LibraryAnalyzer library, SubtitleAnalyzer analyzer,
        SubtitleDownloader downloader, SubtitleProviderSet providers, MediaMatcher matcher, PosterLoader posters, UndoService undo,
        SubtitleEmbedder embedder)
    {
        _shell = shell;
        _settings = settings;
        _library = library;
        _analyzer = analyzer;
        _downloader = downloader;
        _providers = providers;
        _matcher = matcher;
        _posters = posters;
        _undo = undo;
        _embedder = embedder;
        OutputIndex = (int)settings.Current.SubtitleOutput;

        LibraryPath = settings.Current.LastLibraryPath ?? "";
        IsMovies = settings.Current.LastKind == MediaKind.Movie;

        undo.Undone += _ =>
        {
            if (HasResults && !IsBusy) AnalyzeCommand.Execute(null);
        };
    }

    public ObservableCollection<SubtitleFolderViewModel> Folders { get; } = [];
    public ObservableCollection<SubtitleFolderViewModel> VisibleFolders { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];

    public IReadOnlyList<string> FilterOptions { get; } = ["Todas", "Com legendas faltando", "Completas", "Sem correspondência"];

    /// <summary>"Todos os idiomas" + os idiomas das configurações.</summary>
    public IReadOnlyList<string> LanguageOptions { get; private set; } = ["Todos os idiomas"];

    private void RefreshLanguageOptions()
    {
        LanguageOptions = ["Todos os idiomas", .. SubtitleLanguages.NormalizeList(_settings.Current.SubtitleLanguages)];
        OnPropertyChanged(nameof(LanguageOptions));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(RefreshAllCommand))]
    public partial string LibraryPath { get; set; }

    [ObservableProperty]
    public partial bool IsMovies { get; set; }

    [ObservableProperty]
    public partial SubtitleFolderViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasResults { get; private set; }

    public IReadOnlyList<string> OutputOptions { get; } = ["Salvar como arquivo ao lado do vídeo", "Embutir no vídeo (MKV)"];

    /// <summary>0 = arquivo (oculto, se ligado); 1 = embutir no MKV.</summary>
    [ObservableProperty]
    public partial int OutputIndex { get; set; }

    public string OutputHint => OutputIndex == 1
        ? "Os MKV recebem as legendas como faixas (o original fica guardado para desfazer); MP4 e AVI ficam com o arquivo ao lado."
        : _settings.Current.HideSubtitleFiles && HiddenFile.IsSupported
            ? "As legendas são salvas ao lado do vídeo, ocultas. Tudo pode ser desfeito pelo Histórico."
            : "As legendas são salvas ao lado do vídeo. Tudo pode ser desfeito pelo Histórico.";

    partial void OnOutputIndexChanged(int value) => OnPropertyChanged(nameof(OutputHint));

    public bool IsBusy => IsAnalyzing || IsDownloading;

    public int KindIndex
    {
        get => IsMovies ? 1 : 0;
        set => IsMovies = value == 1;
    }

    public bool HasNotices => Notices.Count > 0;

    public int SelectedCount => Folders.Sum(f => f.SelectedCount);
    public int MissingFolderCount => Folders.Count(f => f.IsMissing);
    public int CompleteCount => Folders.Count(f => f.IsComplete);
    public int ProblemCount => Folders.Count(f => f.IsProblem);
    public bool HasMissing => MissingFolderCount > 0;
    public bool HasProblems => ProblemCount > 0;

    public string Summary => SelectedCount == 0
        ? "Nenhuma legenda selecionada"
        : $"{SelectedCount} legenda(s) selecionada(s) em {Folders.Count(f => f.SelectedCount > 0)} pasta(s)";

    /// <summary>"OpenSubtitles: 18 downloads restantes hoje".</summary>
    public string? QuotaText
    {
        get
        {
            var parts = _providers.All.Where(p => p.IsConfigured && p.RemainingDownloads is not null)
                .Select(p => $"{p.Name}: {p.RemainingDownloads} download(s) restante(s) hoje").ToList();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public bool HasQuota => QuotaText is not null;

    partial void OnIsMoviesChanged(bool value)
    {
        OnPropertyChanged(nameof(KindIndex));
        Clear();
    }

    partial void OnLibraryPathChanged(string value) => Clear();

    partial void OnFilterIndexChanged(int value) => RefreshVisible();

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    partial void OnLanguageIndexChanged(int value)
    {
        // A View é recriada ao trocar de página; o ComboBox que sai devolve -1.
        if (value < 0)
        {
            Dispatcher.UIThread.Post(() => LanguageIndex = 0);
            return;
        }

        var code = value <= 0 ? null : LanguageOptions.ElementAtOrDefault(value);
        foreach (var folder in Folders) folder.ApplyLanguageFilter(code);
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var title = IsMovies ? "Escolha a pasta de filmes" : "Escolha a pasta de séries";
        if (await _shell.PickFolderAsync(title, LibraryPath) is { } path) LibraryPath = path;
    }

    private bool CanAnalyze() => Directory.Exists(LibraryPath);

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private Task AnalyzeAsync(CancellationToken ct) => RunAsync(refresh: false, ct);

    /// <summary>Busca de novo nos sites, ignorando o cache (legendas novas para episódios recentes).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task RefreshAllAsync() => RunAsync(refresh: true, CancellationToken.None);

    private async Task RunAsync(bool refresh, CancellationToken ct)
    {
        var previous = SelectedFolder?.FolderName;
        Clear();
        RefreshLanguageOptions();
        LanguageIndex = 0;
        IsAnalyzing = true;
        Progress = 0;
        ProgressText = "Lendo a pasta...";
        RefreshNotices();

        try
        {
            var root = LibraryPath;
            var kind = IsMovies ? MediaKind.Movie : MediaKind.Series;
            var folders = await Task.Run(() => _library.Scan(root, kind), ct);
            if (folders.Count == 0)
            {
                _shell.ShowToast(kind == MediaKind.Series
                    ? "Nenhuma série encontrada. Cada série deve estar em uma subpasta da pasta escolhida."
                    : "Nenhum filme encontrado na pasta escolhida.");
                return;
            }

            var done = 0;
            await foreach (var plan in _analyzer.AnalyzeAsync(folders, refresh, ct))
            {
                var vm = new SubtitleFolderViewModel(plan, this);
                Insert(vm);
                _ = vm.LoadPosterAsync(_posters);
                done++;
                Progress = done * 100.0 / folders.Count;
                ProgressText = $"Procurando legendas {done} de {folders.Count} — {plan.Folder.Name}";
            }

            HasResults = true;
            SelectedFolder = VisibleFolders.FirstOrDefault(f => f.FolderName == previous)
                             ?? VisibleFolders.FirstOrDefault(f => f.IsMissing)
                             ?? VisibleFolders.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            HasResults = Folders.Count > 0;
            _shell.ShowToast("Busca cancelada.");
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
            RefreshNotices();
            NotifyTotals();
        }
    }

    private bool CanDownload() => !IsDownloading && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadSelectedAsync() => DownloadAsync(Folders);

    internal async Task DownloadAsync(IEnumerable<SubtitleFolderViewModel> folders)
    {
        var targets = folders.ToList();
        var downloads = targets.SelectMany(f => f.Items).Select(i => i.ToDownload()).OfType<SubtitleDownload>().ToList();
        if (downloads.Count == 0)
        {
            _shell.ShowToast("Nenhuma legenda selecionada para baixar.");
            return;
        }

        IsDownloading = true;
        Progress = 0;
        ProgressText = $"Baixando {downloads.Count} legenda(s)...";
        try
        {
            var kind = IsMovies ? MediaKind.Movie : MediaKind.Series;
            var description = targets.Count == 1
                ? $"Legendas: {targets[0].Name} ({downloads.Count})"
                : $"Legendas: {downloads.Count} em {downloads.Select(d => d.Folder.Folder.Key).Distinct().Count()} pasta(s)";
            var progress = new Progress<double>(p =>
            {
                Progress = p * 100;
                ProgressText = $"Baixando {Math.Min(downloads.Count, (int)Math.Round(p * downloads.Count) + 1)} de {downloads.Count}...";
            });

            var output = (SubtitleOutput)OutputIndex;
            if (output == SubtitleOutput.Embed && !_embedder.IsAvailable)
            {
                _shell.ShowToast("mkvmerge não encontrado: instale o MKVToolNix ou informe o caminho nas Configurações.", isError: true,
                    "Abrir configurações", () =>
                    {
                        _shell.Navigate(Page.Settings);
                        return Task.CompletedTask;
                    });
                return;
            }

            var result = await _downloader.ApplyAsync(LibraryPath, kind, description, downloads, output, progress);

            if (result.Failed == 0)
            {
                _shell.ShowToast($"{result.Succeeded} legenda(s) baixada(s).", actionText: "Desfazer",
                    action: () => _undo.UndoAsync(result.Journal, confirm: false));
            }
            else
            {
                _shell.ShowToast($"{result.Succeeded} baixada(s), {result.Failed} com erro. Primeiro erro: {result.Errors[0]}", isError: true,
                    actionText: result.Succeeded > 0 ? "Desfazer" : null,
                    action: result.Succeeded > 0 ? () => _undo.UndoAsync(result.Journal, confirm: false) : null);
            }

            foreach (var folder in targets.Where(f => downloads.Any(d => d.Folder == f.Plan)).ToList())
                await ReanalyzeAsync(folder, refresh: false);
        }
        finally
        {
            IsDownloading = false;
            RefreshNotices();
            NotifyTotals();
        }
    }

    /// <summary>Embute no MKV as legendas externas que já estão na pasta (os arquivos vão para ".mediaorganizer/originals").</summary>
    internal async Task EmbedExistingAsync(SubtitleFolderViewModel folder)
    {
        if (!_embedder.IsAvailable)
        {
            _shell.ShowToast("mkvmerge não encontrado: instale o MKVToolNix ou informe o caminho nas Configurações.", isError: true);
            return;
        }

        var videos = folder.EmbeddableVideos.Select(v => new ExistingEmbed(folder.Plan, v.State.VideoPath, v.State.Existing)).ToList();
        var size = videos.Sum(v => SafeLength(v.VideoPath));
        var backup = _settings.Current.KeepOriginalAfterEmbed
            ? "Os vídeos originais ficam guardados em “.mediaorganizer/originals” até você removê-los pelo Histórico."
            : "Os vídeos originais vão para a Lixeira.";
        if (!await _shell.ConfirmAsync("Embutir legendas nos vídeos?",
                $"{videos.Count} vídeo(s) MKV serão regravados com as legendas como faixas; os arquivos de legenda saem da pasta. " +
                $"É preciso ter cerca de {size / (1024.0 * 1024 * 1024):0.0} GB livres. {backup}",
                "Embutir"))
            return;

        IsDownloading = true;
        Progress = 0;
        ProgressText = $"Embutindo legendas em {videos.Count} vídeo(s)...";
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p * 100;
                ProgressText = $"Embutindo {Math.Min(videos.Count, (int)Math.Round(p * videos.Count) + 1)} de {videos.Count}...";
            });
            var result = await _downloader.EmbedExistingAsync(LibraryPath, IsMovies ? MediaKind.Movie : MediaKind.Series,
                $"Legendas embutidas: {folder.Name} ({videos.Count})", videos, progress);

            if (result.Failed == 0)
                _shell.ShowToast($"Legendas embutidas em {result.Succeeded} vídeo(s).", actionText: "Desfazer",
                    action: () => _undo.UndoAsync(result.Journal, confirm: false));
            else
                _shell.ShowToast($"{result.Succeeded} embutido(s), {result.Failed} com erro. Primeiro erro: {result.Errors[0]}", isError: true);

            await ReanalyzeAsync(folder, refresh: false);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    internal async Task IdentifyAsync(SubtitleFolderViewModel folder)
    {
        var dialog = new MatchDialogViewModel(folder.Plan.Folder, _matcher, _posters, folder.Plan.Match);
        if (await _shell.ShowDialogAsync(dialog) is not { } chosen) return;

        _settings.SetMatch(folder.Plan.Folder.MatchKey, chosen.Id);
        await ReanalyzeAsync(folder, refresh: false);
    }

    internal async Task ReanalyzeAsync(SubtitleFolderViewModel folder, bool refresh)
    {
        folder.IsBusy = true;
        try
        {
            var plan = folder.Plan.Folder;
            var current = plan.IsLooseFile
                ? _library.Scan(plan.RootPath, plan.Kind).FirstOrDefault(f => f.IsLooseFile && f.Name == plan.Name) ?? plan
                : _library.CreateScanner().ScanFolder(plan.Kind, plan.RootPath, plan.Path);
            folder.Replace(await _analyzer.AnalyzeFolderAsync(current, refresh, CancellationToken.None));
            await folder.LoadPosterAsync(_posters);
        }
        catch (TmdbAuthenticationException e)
        {
            _shell.ShowToast(e.Message, isError: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast($"Não foi possível ler a pasta: {e.Message}", isError: true);
        }
        finally
        {
            folder.IsBusy = false;
            NotifyTotals();
            RefreshVisible();
        }
    }

    [RelayCommand]
    private void OpenSettings() => _shell.Navigate(Page.Settings);

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(Summary));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RefreshNotices()
    {
        Notices.Clear();
        foreach (var notice in _providers.Notices()) Notices.Add(notice);
        OnPropertyChanged(nameof(HasNotices));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(HasQuota));
    }

    private void Insert(SubtitleFolderViewModel folder)
    {
        var index = 0;
        while (index < Folders.Count && CompareNames(Folders[index].Name, folder.Name) < 0) index++;
        Folders.Insert(index, folder);
        if (Matches(folder)) VisibleFolders.Insert(VisibleFolders.TakeWhile(f => CompareNames(f.Name, folder.Name) < 0).Count(), folder);
        NotifyTotals();
    }

    private static int CompareNames(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase);

    private void RefreshVisible()
    {
        var selected = SelectedFolder;
        VisibleFolders.Clear();
        foreach (var folder in Folders.Where(Matches)) VisibleFolders.Add(folder);
        SelectedFolder = selected is not null && VisibleFolders.Contains(selected) ? selected : VisibleFolders.FirstOrDefault();
    }

    private bool Matches(SubtitleFolderViewModel folder)
    {
        var byStatus = (SubtitleFilter)FilterIndex switch
        {
            SubtitleFilter.Missing => folder.IsMissing,
            SubtitleFilter.Complete => folder.IsComplete,
            SubtitleFilter.NoMatch => folder.IsProblem,
            _ => true,
        };
        return byStatus && (SearchText.Length == 0 ||
                            folder.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            folder.FolderName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Clear()
    {
        Folders.Clear();
        VisibleFolders.Clear();
        SelectedFolder = null;
        HasResults = false;
        NotifyTotals();
    }

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(MissingFolderCount));
        OnPropertyChanged(nameof(CompleteCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasProblems));
        SelectionChanged();
    }
}
