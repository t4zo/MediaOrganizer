using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.App.ViewModels;

public sealed partial class SettingsViewModel(
    ShellServices shell, SettingsStore settings, ProcessedStore processed, MetadataCache cache, DataPaths paths, TmdbClient tmdb)
    : ObservableObject
{
    [ObservableProperty]
    public partial string ApiKey { get; set; } = "";

    [ObservableProperty]
    public partial bool RenameSubtitles { get; set; }

    [ObservableProperty]
    public partial string IgnoredFolders { get; set; } = "";

    [ObservableProperty]
    public partial string JunkPatterns { get; set; } = "";

    [ObservableProperty]
    public partial string? KeyStatus { get; private set; }

    [ObservableProperty]
    public partial bool? KeyValid { get; private set; }

    [ObservableProperty]
    public partial int ProcessedCount { get; private set; }

    public string DataFolder => paths.Root;

    public SettingsViewModel Reloaded()
    {
        var s = settings.Current;
        ApiKey = s.TmdbApiKey ?? "";
        RenameSubtitles = s.RenameSubtitles;
        IgnoredFolders = string.Join(Environment.NewLine, s.IgnoredFolders);
        JunkPatterns = string.Join(Environment.NewLine, s.JunkPatterns);
        ProcessedCount = processed.Count;
        KeyStatus = null;
        KeyValid = null;
        return this;
    }

    [RelayCommand]
    private async Task TestKeyAsync()
    {
        try
        {
            KeyValid = await tmdb.ValidateKeyAsync(ApiKey);
            KeyStatus = KeyValid == true ? "Chave válida." : "Chave recusada pelo TMDb.";
        }
        catch (HttpRequestException e)
        {
            KeyValid = false;
            KeyStatus = $"Sem conexão com o TMDb: {e.Message}";
        }
    }

    [RelayCommand]
    private void Save()
    {
        settings.Update(s =>
        {
            s.TmdbApiKey = ApiKey.Trim();
            s.RenameSubtitles = RenameSubtitles;
            s.IgnoredFolders = SplitLines(IgnoredFolders);
            s.JunkPatterns = SplitLines(JunkPatterns);
        });
        shell.ShowToast("Configurações salvas.");
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => shell.RevealFolderAsync(paths.Root);

    [RelayCommand]
    private Task OpenTmdbAsync() => shell.OpenUrlAsync(OnboardingViewModel.TmdbApiUrl);

    [RelayCommand]
    private void ClearCache()
    {
        cache.Clear();
        shell.ShowToast("Cache do TMDb limpo.");
    }

    [RelayCommand]
    private async Task ClearProcessedAsync()
    {
        if (!await shell.ConfirmAsync("Esquecer pastas processadas?",
                "Na próxima análise todas as pastas serão verificadas novamente, mesmo sem alterações. Nenhum arquivo é modificado.",
                "Esquecer"))
            return;

        processed.Clear();
        ProcessedCount = 0;
        shell.ShowToast("Registro de pastas processadas apagado.");
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
