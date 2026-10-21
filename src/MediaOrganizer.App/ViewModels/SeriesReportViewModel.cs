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
        Seasons = [.. report.RegularSeasons.Select(s => new SeasonReportViewModel(s))];
        Specials = report.Specials is { } specials ? new SeasonReportViewModel(specials) : null;
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
        Seasons = [.. report.RegularSeasons.Select(s => new SeasonReportViewModel(s))];
        Specials = report.Specials is { } specials ? new SeasonReportViewModel(specials) : null;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task IdentifyAsync() => _owner.IdentifyAsync(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.ReanalyzeAsync(this, refresh: true);

    [RelayCommand]
    private Task OpenTmdbAsync() => Report.Show is { } show ? _owner.OpenUrlAsync(show.WebUrl) : Task.CompletedTask;
}

/// <summary>Uma temporada no detalhe da Coleção.</summary>
public sealed class SeasonReportViewModel(SeasonReport season)
{
    public SeasonReport Season { get; } = season;

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

    public IReadOnlyList<string> MissingLines { get; } = [.. season.Missing.Select(Format)];
    public IReadOnlyList<string> UpcomingLines { get; } = [.. season.Upcoming.Select(Format)];
    public bool HasMissing => MissingLines.Count > 0;
    public bool HasUpcoming => UpcomingLines.Count > 0;

    private static string Format(EpisodeRef episode)
    {
        var date = episode.AirDate?.ToString("d", CultureInfo.CurrentCulture) ?? "data a definir";
        return string.IsNullOrEmpty(episode.Title) ? $"{episode.Code} · {date}" : $"{episode.Code} · {episode.Title} · {date}";
    }
}
