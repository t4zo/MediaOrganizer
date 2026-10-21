using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Uma série ou filme na tela Legendas.</summary>
public sealed partial class SubtitleFolderViewModel : ObservableObject
{
    private readonly SubtitlesViewModel _owner;
    private string? _languageFilter;

    public SubtitleFolderViewModel(FolderSubtitlePlan plan, SubtitlesViewModel owner)
    {
        _owner = owner;
        Plan = plan;
        Videos = BuildVideos(plan);
    }

    private IReadOnlyList<SubtitleVideoViewModel> BuildVideos(FolderSubtitlePlan plan) =>
        [.. plan.Videos.Where(v => v.Items.Count > 0 || v.State.Existing.Count > 0).Select(v => new SubtitleVideoViewModel(v, this))];

    public FolderSubtitlePlan Plan { get; private set; }

    public IReadOnlyList<SubtitleVideoViewModel> Videos { get; private set; }

    public IEnumerable<SubtitleVideoViewModel> VisibleVideos => Videos.Where(v => v.IsVisible);

    public IEnumerable<SubtitleItemViewModel> Items => Videos.SelectMany(v => v.Items);

    public string Name => Plan.Match?.PreferredTitle ?? Plan.Folder.Name;
    public string FolderName => Plan.Folder.Name;
    public bool HasMatch => Plan.Match is not null;
    public string? Message => Plan.Message;
    public bool HasMessage => Plan.Message is not null;
    public bool IsMovie => Plan.Folder.Kind == Core.MediaKind.Movie;

    public bool IsComplete => Plan.Status == FolderSubtitleStatus.Complete;
    public bool IsMissing => Plan.Status == FolderSubtitleStatus.Missing;
    public bool IsProblem => Plan.Status is FolderSubtitleStatus.NoMatch or FolderSubtitleStatus.Error;
    public bool CanIdentify => Plan.Status != FolderSubtitleStatus.Error || Plan.Match is not null;

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool HasVisibleVideos => VisibleVideos.Any();

    public string StatusText => Plan.Status switch
    {
        FolderSubtitleStatus.Complete => "Completo",
        FolderSubtitleStatus.Missing => MissingText,
        FolderSubtitleStatus.NoMatch => "Sem correspondência",
        _ => "Erro",
    };

    /// <summary>"Faltam 8 pt-BR · 3 en".</summary>
    public string MissingText => "Faltam " + string.Join(" · ", Plan.MissingByLanguage.Select(m => $"{m.Count} {m.Language}"));

    public string FoundText => Plan.MissingCount == 0 ? "" : string.Create(CultureInfo.CurrentCulture, $"{Plan.FoundCount} de {Plan.MissingCount} encontradas");

    [ObservableProperty]
    public partial Bitmap? Poster { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    internal async Task LoadPosterAsync(Services.PosterLoader loader) => Poster = await loader.LoadAsync(Plan.Match?.PosterUrl);

    internal void Replace(FolderSubtitlePlan plan)
    {
        Plan = plan;
        Videos = BuildVideos(plan);
        ApplyLanguageFilter(_languageFilter);
        OnPropertyChanged(string.Empty);
    }

    internal void ApplyLanguageFilter(string? code)
    {
        _languageFilter = code;
        foreach (var video in Videos) video.ApplyLanguageFilter(code);
        OnPropertyChanged(nameof(VisibleVideos));
        OnPropertyChanged(nameof(HasVisibleVideos));
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        _owner.SelectionChanged();
    }

    [RelayCommand]
    private void SelectAll(bool selected)
    {
        foreach (var item in Items.Where(i => i.CanSelect && i.IsVisible)) item.IsSelected = selected;
    }

    /// <summary>"Baixar faltantes": as selecionadas desta pasta, num clique.</summary>
    [RelayCommand]
    private Task DownloadAsync() => _owner.DownloadAsync([this]);

    [RelayCommand]
    private Task IdentifyAsync() => _owner.IdentifyAsync(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.ReanalyzeAsync(this, refresh: true);
}

/// <summary>Um vídeo: as legendas que já tem e as que faltam.</summary>
public sealed partial class SubtitleVideoViewModel : ObservableObject
{
    public SubtitleVideoViewModel(VideoSubtitlePlan plan, SubtitleFolderViewModel folder)
    {
        Plan = plan;
        Items = [.. plan.Items.Select(i => new SubtitleItemViewModel(i, plan.Query, folder))];
    }

    public VideoSubtitlePlan Plan { get; }

    public IReadOnlyList<SubtitleItemViewModel> Items { get; }

    public IEnumerable<SubtitleItemViewModel> VisibleItems => Items.Where(i => i.IsVisible);

    public string Title => Path.GetFileNameWithoutExtension(Plan.State.VideoPath);

    public string? Code => Plan.State.Episode is { } e ? $"S{e.Season:00}E{e.Episode:00}" : null;

    /// <summary>"Já tem: pt-BR (embutida), en".</summary>
    public string ExistingText => Plan.State.Existing.Count == 0
        ? "Nenhuma legenda"
        : "Já tem: " + string.Join(", ", Plan.State.Existing
            .Select(e => e.Language + (e.Embedded ? " (embutida)" : ""))
            .Distinct());

    public bool IsComplete => Items.Count == 0;

    [ObservableProperty]
    public partial bool IsVisible { get; private set; } = true;

    internal void ApplyLanguageFilter(string? code)
    {
        foreach (var item in Items) item.IsVisible = code is null || item.Item.Wanted.Code.Equals(code, StringComparison.OrdinalIgnoreCase);
        IsVisible = code is null ? true : Items.Any(i => i.IsVisible);
        OnPropertyChanged(nameof(VisibleItems));
    }
}

/// <summary>Uma opção do menu de candidatos: "OpenSubtitles · Show.S01E01.WEB-DL-NTb · hash".</summary>
public sealed record CandidateOption(SubtitleCandidate Candidate)
{
    public string Display
    {
        get
        {
            var tags = new List<string> { Candidate.Provider };
            if (!string.IsNullOrWhiteSpace(Candidate.ReleaseName)) tags.Add(Candidate.ReleaseName);
            if (Candidate.HashMatch) tags.Add("hash");
            if (Candidate.Language.HearingImpaired) tags.Add("SDH");
            if (Candidate.IsSeasonPack) tags.Add("pacote da temporada");
            if (Candidate.MachineTranslated) tags.Add("tradução automática");
            return string.Join(" · ", tags);
        }
    }

    public override string ToString() => Display;
}

/// <summary>Uma legenda faltando: idioma, candidato escolhido (trocável) e seleção.</summary>
public sealed partial class SubtitleItemViewModel : ObservableObject
{
    private readonly SubtitleFolderViewModel _folder;
    private readonly bool _ready;

    public SubtitleItemViewModel(SubtitleItem item, SubtitleQuery? query, SubtitleFolderViewModel folder)
    {
        _folder = folder;
        Item = item;
        Query = query;
        Options = [.. item.Candidates.Select(c => new CandidateOption(c))];
        SelectedOption = Options.Count > 0 ? Options[0] : null;
        IsSelected = CanSelect && !item.NeedsConfirmation;
        _ready = true;
    }

    public SubtitleItem Item { get; }
    public SubtitleQuery? Query { get; }
    public IReadOnlyList<CandidateOption> Options { get; }

    public string LanguageText => Item.Wanted.ToString();
    public string LanguageName => Item.Wanted.DisplayName;
    public bool HasOptions => Options.Count > 0;
    public bool HasAlternatives => Options.Count > 1;

    public bool IsFound => Item.Status == SubtitleItemStatus.Found;
    public bool IsNotFound => Item.Status == SubtitleItemStatus.NotFound;
    public bool IsProblem => Item.Status is SubtitleItemStatus.Conflict or SubtitleItemStatus.Error;

    public string? Message => TargetExists ? "Já existe um arquivo com este nome." : Item.Message;
    public bool HasMessage => Message is not null;

    public bool HashMatch => SelectedOption?.Candidate.HashMatch == true;

    public bool CanSelect => IsFound && Query is not null && SelectedOption is not null && !TargetExists;

    private bool TargetExists => SelectedOption is { } option && Query is not null &&
                                 File.Exists(SubtitleItem.TargetPath(Query.VideoPath, option.Candidate));

    public string StatusText => Item.Status switch
    {
        SubtitleItemStatus.Found => "Encontrada",
        SubtitleItemStatus.NotFound => "Nenhuma encontrada",
        SubtitleItemStatus.Conflict => "Conflito",
        _ => "Erro",
    };

    [ObservableProperty]
    public partial CandidateOption? SelectedOption { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    partial void OnSelectedOptionChanged(CandidateOption? value)
    {
        OnPropertyChanged(nameof(HashMatch));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
        if (!CanSelect) IsSelected = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            IsSelected = false;
            return;
        }
        if (_ready) _folder.SelectionChanged();
    }

    internal SubtitleDownload? ToDownload() =>
        IsSelected && CanSelect && SelectedOption is { } option ? new SubtitleDownload(_folder.Plan, Query!, option.Candidate) : null;
}
