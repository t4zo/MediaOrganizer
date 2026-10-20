using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Busca manual no TMDb para corrigir a identificação de uma série/filme.</summary>
public sealed partial class MatchDialogViewModel : DialogViewModel<MediaCandidate>
{
    private readonly MediaMatcher _matcher;
    private readonly PosterLoader _posters;
    private readonly MediaKind _kind;

    public MatchDialogViewModel(LibraryFolder folder, MediaMatcher matcher, PosterLoader posters, MediaCandidate? current)
    {
        _matcher = matcher;
        _posters = posters;
        _kind = folder.Kind;
        FolderName = folder.Name;

        var parsed = ReleaseNameParser.ParseMovie(folder.Name);
        Query = parsed.Title.Length > 0 ? parsed.Title : folder.Name;
        Year = parsed.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        CurrentId = current?.Id;

        _ = SearchAsync();
    }

    public string FolderName { get; }
    public int? CurrentId { get; }
    public string Title => _kind == MediaKind.Series ? "Escolher série" : "Escolher filme";

    public ObservableCollection<CandidateViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial string Year { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial CandidateViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        IsSearching = true;
        Status = null;
        try
        {
            int? year = int.TryParse(Year, out var y) ? y : null;
            var results = await _matcher.SearchAsync(_kind, Query.Trim(), year, LanguagePolicy.English, CancellationToken.None);
            if (results.Count == 0 && year is not null)
                results = await _matcher.SearchAsync(_kind, Query.Trim(), null, LanguagePolicy.English, CancellationToken.None);
            if (results.Count == 0)
                results = await _matcher.SearchAsync(_kind, Query.Trim(), null, "pt-BR", CancellationToken.None);

            Results.Clear();
            foreach (var candidate in results.Take(20))
            {
                var vm = new CandidateViewModel(candidate, candidate.Id == CurrentId);
                Results.Add(vm);
                _ = vm.LoadPosterAsync(_posters);
            }

            Selected = Results.FirstOrDefault(r => r.IsCurrent) ?? Results.FirstOrDefault();
            if (Results.Count == 0) Status = "Nada encontrado. Tente o título original ou em inglês.";
        }
        catch (Exception e) when (e is HttpRequestException or TmdbAuthenticationException)
        {
            Status = e.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanConfirm() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (Selected is null) return;
        // Detalhes em inglês para ter o nome canônico (a busca pode ter vindo em pt-BR).
        var details = await _matcher.GetDetailsAsync(_kind, Selected.Candidate.Id, CancellationToken.None) ?? Selected.Candidate;
        Close(details);
    }
}

public sealed partial class CandidateViewModel(MediaCandidate candidate, bool isCurrent) : ObservableObject
{
    public MediaCandidate Candidate { get; } = candidate;
    public bool IsCurrent { get; } = isCurrent;
    public string Title => Candidate.DisplayName;
    public string? Original => Candidate.OriginalTitle != Candidate.Title ? Candidate.OriginalTitle : null;
    public string? Overview => Candidate.Overview;

    [ObservableProperty]
    public partial Bitmap? Poster { get; private set; }

    internal async Task LoadPosterAsync(PosterLoader loader) => Poster = await loader.LoadAsync(Candidate.PosterUrl);
}
