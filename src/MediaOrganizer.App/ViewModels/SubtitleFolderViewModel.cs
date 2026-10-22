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

    internal SubtitlesViewModel Owner => _owner;

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

    /// <summary>Um vídeo ganhou opções de outros sites: atualiza o plano da pasta e só esse vídeo na tela.</summary>
    internal void ReplaceVideo(SubtitleVideoViewModel video, VideoSubtitlePlan updated)
    {
        Plan = Plan with { Videos = [.. Plan.Videos.Select(v => ReferenceEquals(v, video.Plan) ? updated : v)] };
        video.Replace(updated);
        video.ApplyLanguageFilter(_languageFilter);
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(FoundText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MissingText));
        SelectionChanged();
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

    /// <summary>Vídeos MKV com legendas externas de texto que podem ser embutidas.</summary>
    public IEnumerable<VideoSubtitlePlan> EmbeddableVideos => Plan.Videos.Where(v =>
        MatroskaReader.IsMatroska(v.State.VideoPath) &&
        v.State.Existing.Any(e => !e.Embedded && e.Path is not null && SubtitleText.IsText(Path.GetExtension(e.Path))));

    public bool CanEmbedExisting => EmbeddableVideos.Any();

    [RelayCommand]
    private Task EmbedExistingAsync() => _owner.EmbedExistingAsync(this);

    [RelayCommand]
    private Task IdentifyAsync() => _owner.IdentifyAsync(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.ReanalyzeAsync(this, refresh: true);
}

/// <summary>Um vídeo: as legendas que já tem e as que faltam.</summary>
public sealed partial class SubtitleVideoViewModel : ObservableObject
{
    private readonly SubtitleFolderViewModel _folder;
    private SyncReferenceChoice? _reference;
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);

    /// <summary>Resultados de todas as verificações do vídeo (os itens são recriados quando chegam opções de outros sites).</summary>
    private readonly Dictionary<string, CandidateSync> _results = new(StringComparer.Ordinal);

    public SubtitleVideoViewModel(VideoSubtitlePlan plan, SubtitleFolderViewModel folder)
    {
        _folder = folder;
        Plan = plan;
        Items = BuildItems(plan);
    }

    private IReadOnlyList<SubtitleItemViewModel> BuildItems(VideoSubtitlePlan plan) =>
        [.. plan.Items.Select(i => new SubtitleItemViewModel(i, plan.Query, _folder))];

    internal SubtitleFolderViewModel Folder => _folder;

    public VideoSubtitlePlan Plan { get; private set; }

    public IReadOnlyList<SubtitleItemViewModel> Items { get; private set; }

    /// <summary>Opções novas de outros sites: refaz os itens, mantendo marcadas/desmarcadas como o usuário deixou.</summary>
    internal void Replace(VideoSubtitlePlan plan)
    {
        // Só vale a escolha de itens que já podiam ser marcados; os que eram "nenhuma encontrada" seguem o padrão.
        var selected = Items.Where(i => i.CanSelect).ToDictionary(i => i.Item.Wanted, i => i.IsSelected);
        Plan = plan;
        Items = BuildItems(plan);
        foreach (var item in Items)
        {
            if (selected.TryGetValue(item.Item.Wanted, out var wasSelected)) item.IsSelected = wasSelected && item.CanSelect;
        }
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasCandidates));
        NotifyVerify();
        OnPropertyChanged(nameof(VerifyTip));
    }

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

    // Verificação de sincronia

    /// <summary>Há opções para comparar (o botão só aparece nesses vídeos).</summary>
    public bool HasCandidates => Plan.Query is not null && Items.Any(i => i.HasOptions);

    /// <summary>Legenda já presente usada como referência (calculada só quando a tela precisa).</summary>
    private SyncReferenceChoice Reference => _reference ??= _folder.Owner.SyncReference(Plan);

    public bool CanVerify => HasCandidates && Reference.IsAvailable && UnverifiedCount > 0;

    /// <summary>Opções que ainda faltam verificar (todas as legendas do vídeo).</summary>
    public int UnverifiedCount => SubtitleSyncChecker.Unverified(Plan, _verified);

    public string VerifyText => _verified.Count == 0 ? "Verificar sincronia" : UnverifiedCount > 0 ? $"Verificar mais ({UnverifiedCount})" : "Tudo verificado";

    internal IReadOnlySet<string> Verified => _verified;

    private void NotifyVerify()
    {
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(UnverifiedCount));
        OnPropertyChanged(nameof(VerifyText));
    }

    public string VerifyTip => Reference.Source is { } source
        ? "Verificar sincronia: procura opções também nos sites que ficaram de fora da busca, baixa as de cada legenda " +
          $"(do OpenSubtitles, até {_folder.Owner.SyncOpenSubtitlesLimit} por clique; SubDL e SubSource, todas) e compara os tempos com a legenda " +
          $"{SubtitleReferenceProvider.Describe(source)}. {_folder.Owner.SyncDownloadsText(Plan, _verified)}"
        : $"Não é possível verificar a sincronia: {Reference.Reason}";

    [ObservableProperty]
    public partial bool IsVerifying { get; set; }

    [ObservableProperty]
    public partial double VerifyProgress { get; set; }

    /// <summary>"Sincronia comparada com a legenda en (embutida)", depois de verificar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncSummary))]
    public partial string? SyncSummary { get; private set; }

    public bool HasSyncSummary => SyncSummary is not null;

    [RelayCommand(IncludeCancelCommand = true)]
    private Task VerifySyncAsync(CancellationToken ct) => _folder.Owner.VerifySyncAsync(this, ct);

    internal void ApplySync(VideoSyncReport report, MoreSubtitlesResult more)
    {
        foreach (var result in report.Candidates) _results[SubtitleSyncChecker.Key(result.Candidate)] = result;
        foreach (var item in Items) item.ApplySync([.. _results.Values], _verified);
        if (report.Candidates.Count > 0)
        {
            foreach (var option in Items.SelectMany(i => i.Options))
                option.NotVerified = !_verified.Contains(SubtitleSyncChecker.Key(option.Candidate)) && option.Sync is null && option.SyncError is null;
        }

        var searched = more.Searched.Count == 0 ? ""
            : more.Added > 0 ? $" Também buscado em {string.Join(" e ", more.Searched)}: {more.Added} opção(ões) nova(s)."
            : $" Também buscado em {string.Join(" e ", more.Searched)}: nada novo.";
        if (report.Reference is { } reference && report.Candidates.Count > 0)
            SyncSummary = $"Sincronia comparada com a legenda {reference.Description}.{searched}";
        else if (searched.Length > 0)
            SyncSummary = searched.Trim();
        OnPropertyChanged(nameof(VerifyTip));
        NotifyVerify();
    }
}

/// <summary>Uma opção do menu de candidatos: "OpenSubtitles · Show.S01E01.WEB-DL-NTb · hash · 96% sincronizada".</summary>
public sealed partial class CandidateOption(SubtitleCandidate candidate) : ObservableObject
{
    public SubtitleCandidate Candidate { get; } = candidate;

    /// <summary>Resultado da verificação de sincronia (<c>null</c> se não verificada ou se falhou).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    public partial SyncResult? Sync { get; set; }

    /// <summary>Por que a verificação desta opção falhou (cota, formato de imagem...).</summary>
    [ObservableProperty]
    public partial string? SyncError { get; set; }

    /// <summary>A verificação rodou no vídeo, mas esta opção ficou de fora (limite por clique).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    public partial bool NotVerified { get; set; }

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
            if (Sync is { Verdict: not SyncVerdict.Hash } sync) tags.Add(sync.Label);
            else if (NotVerified) tags.Add("não verificada");
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
    private bool _reordering;
    private bool _userChose;

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
    public IReadOnlyList<CandidateOption> Options { get; private set; }

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

    // Sincronia da opção escolhida: selo "96%" (verde), "41%" (amarelo, deslocada) ou "não confere" (vermelho).

    private SyncResult? Sync => SelectedOption?.Sync is { Verdict: not SyncVerdict.Hash } sync ? sync : null;

    public bool HasSync => Sync is not null;
    public string? SyncText => Sync is { } sync ? sync.Verdict == SyncVerdict.Mismatch ? "não confere" : $"{sync.Percent}%" : null;
    public bool SyncGood => Sync?.Verdict == SyncVerdict.Synced;
    public bool SyncWarn => Sync?.Verdict is SyncVerdict.Shifted or SyncVerdict.FrameRate;
    public bool SyncBad => Sync?.Verdict == SyncVerdict.Mismatch;
    public string? SyncTip => Sync?.Description;

    public string? SyncError => SelectedOption?.SyncError is { } error ? $"Sincronia não verificada: {error}" : null;
    public bool HasSyncError => SyncError is not null;

    /// <summary>
    /// Marca cada opção com o resultado e reordena: verificadas primeiro (melhor sincronia no topo), depois as
    /// demais na ordem original. Se o usuário não trocou a opção à mão, a melhor passa a ser a escolhida.
    /// </summary>
    /// <param name="verified">Opções já verificadas no vídeo; esta chamada acrescenta as que acabou de verificar.</param>
    internal void ApplySync(IReadOnlyList<CandidateSync> results, HashSet<string> verified)
    {
        foreach (var option in Options)
        {
            var key = SubtitleSyncChecker.Key(option.Candidate);
            if (results.FirstOrDefault(r => SubtitleSyncChecker.Key(r.Candidate) == key) is not { } result) continue;
            option.Sync = result.Result;
            option.SyncError = result.Result is null ? result.Error : null;
            if (!result.CanRetry) verified.Add(SubtitleSyncChecker.Key(option.Candidate));
        }

        var selected = SelectedOption;
        var wasSelected = IsSelected;
        _reordering = true;
        try
        {
            Options = [.. Options.Where(o => o.Sync is not null).OrderByDescending(o => o.Sync!.SortKey), .. Options.Where(o => o.Sync is null)];
            OnPropertyChanged(nameof(Options));
            SelectedOption = _userChose || Options.Count == 0 ? selected : Options[0];
            // Trocar a lista do ComboBox pode zerar a seleção por um instante, o que desmarcaria o item.
            IsSelected = wasSelected && CanSelect;
        }
        finally
        {
            _reordering = false;
        }
        NotifySync();
    }

    private void NotifySync()
    {
        OnPropertyChanged(nameof(HasSync));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(SyncGood));
        OnPropertyChanged(nameof(SyncWarn));
        OnPropertyChanged(nameof(SyncBad));
        OnPropertyChanged(nameof(SyncTip));
        OnPropertyChanged(nameof(SyncError));
        OnPropertyChanged(nameof(HasSyncError));
    }

    partial void OnSelectedOptionChanged(CandidateOption? value)
    {
        if (_ready && !_reordering && value is not null) _userChose = true;
        NotifySync();
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
