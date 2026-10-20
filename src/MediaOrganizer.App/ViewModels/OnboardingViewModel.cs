using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Primeira execução: API key do TMDb e pasta da biblioteca.</summary>
public sealed partial class OnboardingViewModel(ShellServices shell, SettingsStore settings, TmdbClient tmdb) : ObservableObject
{
    public const string TmdbApiUrl = "https://www.themoviedb.org/settings/api";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    public partial string ApiKey { get; set; } = settings.Current.TmdbApiKey ?? "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    public partial string LibraryPath { get; set; } = settings.Current.LastLibraryPath ?? "";

    [ObservableProperty]
    public partial bool IsMovies { get; set; }

    [ObservableProperty]
    public partial string? KeyStatus { get; private set; }

    [ObservableProperty]
    public partial bool? KeyValid { get; private set; }

    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    partial void OnApiKeyChanged(string value)
    {
        KeyValid = null;
        KeyStatus = null;
    }

    [RelayCommand]
    private Task OpenTmdbAsync() => shell.OpenUrlAsync(TmdbApiUrl);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await shell.PickFolderAsync("Escolha a pasta da biblioteca", LibraryPath) is { } path) LibraryPath = path;
    }

    [RelayCommand]
    private async Task<bool> TestKeyAsync()
    {
        IsTesting = true;
        try
        {
            KeyValid = await tmdb.ValidateKeyAsync(ApiKey);
            KeyStatus = KeyValid == true ? "Chave válida." : "Chave recusada pelo TMDb. Confira se copiou a chave inteira.";
        }
        catch (HttpRequestException e)
        {
            KeyValid = false;
            KeyStatus = $"Não foi possível falar com o TMDb: {e.Message}";
        }
        finally
        {
            IsTesting = false;
        }
        return KeyValid == true;
    }

    private bool CanFinish() => !string.IsNullOrWhiteSpace(ApiKey) && Directory.Exists(LibraryPath);

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task FinishAsync()
    {
        if (KeyValid != true && !await TestKeyAsync()) return;

        settings.Update(s =>
        {
            s.TmdbApiKey = ApiKey.Trim();
            s.LastLibraryPath = LibraryPath;
            s.LastKind = IsMovies ? MediaKind.Movie : MediaKind.Series;
        });
        shell.Navigate(Page.Organize);
    }
}
