using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.Core.Collection;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Uma série na tela Coleção.</summary>
public sealed partial class SeriesReportViewModel : ObservableObject
{
    private readonly CollectionViewModel _owner;

    public SeriesReportViewModel(SeriesReport report, CollectionViewModel owner)
    {
        _owner = owner;
        Report = report;
        (Seasons, Specials) = BuildSeasons(report);
    }

    /// <summary>A primeira temporada começa expandida; as demais e os especiais, recolhidos.</summary>
    private static (IReadOnlyList<SeasonReportViewModel>, SeasonReportViewModel?) BuildSeasons(SeriesReport report)
    {
        IReadOnlyList<SeasonReportViewModel> seasons = [.. report.RegularSeasons.Select((s, i) => new SeasonReportViewModel(s) { IsExpanded = i == 0 })];
        var specials = report.Specials is { } s0 ? new SeasonReportViewModel(s0) { IsExpanded = seasons.Count == 0 } : null;
        return (seasons, specials);
    }

    public SeriesReport Report { get; private set; }

    public IReadOnlyList<SeasonReportViewModel> Seasons { get; private set; }
    public SeasonReportViewModel? Specials { get; private set; }
    public bool HasSpecials => Specials is not null;

    public string Name => Report.Show?.PreferredTitle ?? Report.Folder.Name;
    public string FolderName => Report.Folder.Name;
    public bool HasShow => Report.Show is not null;
    public string? Message => Report.Message;
    public bool HasMessage => Report.Message is not null;

    public bool IsComplete => Report.Status == CollectionStatus.Complete;
    public bool IsIncomplete => Report.Status == CollectionStatus.Incomplete;
    public bool IsProblem => Report.Status is CollectionStatus.NoMatch or CollectionStatus.Error;
    public bool CanIdentify => Report.Status != CollectionStatus.Error;

    public string StatusText => Report.Status switch
    {
        CollectionStatus.Complete => "Completa",
        CollectionStatus.Incomplete => $"Faltam {Report.MissingCount}",
        CollectionStatus.NoMatch => "Sem correspondência",
        _ => "Erro",
    };

    public string ProgressText => Report.Aired == 0 ? "Nenhum episódio exibido" : $"{Report.Present}/{Report.Aired} episódios";
    public double ProgressPercent => Report.Aired == 0 ? 0 : Report.Present * 100.0 / Report.Aired;

    [ObservableProperty]
    public partial Bitmap? Poster { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    internal async Task LoadPosterAsync(Services.PosterLoader loader) => Poster = await loader.LoadAsync(Report.Show?.PosterUrl);

    internal void Replace(SeriesReport report)
    {
        Report = report;
        (Seasons, Specials) = BuildSeasons(report);
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task IdentifyAsync() => _owner.IdentifyAsync(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.ReanalyzeAsync(this, refresh: true);

    [RelayCommand]
    private Task OpenTmdbAsync() => Report.Show is { } show ? _owner.OpenUrlAsync(show.WebUrl) : Task.CompletedTask;
}

/// <summary>Uma temporada no detalhe da Coleção, com todos os episódios e a situação de cada um.</summary>
public sealed partial class SeasonReportViewModel(SeasonReport season) : ObservableObject
{
    public SeasonReport Season { get; } = season;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public IReadOnlyList<EpisodeRowViewModel> Episodes { get; } = [.. season.Episodes.Select(e => new EpisodeRowViewModel(e))];

    public string Title => Season.IsSpecials ? "Especiais" : $"Temporada {Season.Season}";
    public string CountText => Season.Aired == 0 ? "" : $"{Season.Present}/{Season.Aired}";

    public bool IsComplete => Season.Aired > 0 && Season.IsComplete;
    public bool IsIncomplete => !Season.IsComplete;
    public bool IsWaiting => Season.Aired == 0;

    public string StateText =>
        IsWaiting ? "Aguardando estreia"
        : Season.IsNotStarted ? "Não iniciada"
        : Season.IsComplete ? "Completa"
        : $"Faltam {Season.Missing.Count}";

}

/// <summary>Uma linha de episódio: código, título, data de exibição e situação.</summary>
public sealed class EpisodeRowViewModel(EpisodeStatus status)
{
    public string Code { get; } = status.Episode.Code;
    public string Title { get; } = status.Episode.Title ?? "(sem título)";
    public string DateText { get; } = status.Episode.AirDate?.ToString("d", CultureInfo.CurrentCulture) ?? "data a definir";

    public bool IsPresent => status.State == EpisodeState.Present;
    public bool IsMissing => status.State == EpisodeState.Missing;
    public bool IsUpcoming => status.State == EpisodeState.Upcoming;
    public bool IsPresentNotAired => status.State == EpisodeState.PresentNotAired;

    public string StateText => status.State switch
    {
        EpisodeState.Present => "Na pasta",
        EpisodeState.Missing => "Faltando",
        EpisodeState.Upcoming => "Não exibido",
        _ => "Na pasta (ainda não exibido)",
    };
}
