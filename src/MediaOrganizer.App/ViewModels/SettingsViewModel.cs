using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;
using MediaOrganizer.Core.Subtitles.Providers;

namespace MediaOrganizer.App.ViewModels;

public sealed partial class SettingsViewModel(
    ShellServices shell, SettingsStore settings, ProcessedStore processed, MetadataCache cache, DataPaths paths, TmdbClient tmdb,
    SubtitleProviderSet subtitleProviders, IHttpClientFactory httpFactory)
    : ObservableObject
{
    public const string OpenSubtitlesKeyUrl = "https://www.opensubtitles.com/consumers";

    // ---- Legendas ----

    /// <summary>Idiomas em ordem de prioridade, separados por vírgula ("pt-BR, en").</summary>
    [ObservableProperty]
    public partial string SubtitleLanguages { get; set; } = "";

    /// <summary>0 = todos os idiomas da lista; 1 = só o primeiro disponível.</summary>
    [ObservableProperty]
    public partial int LanguageModeIndex { get; set; }

    /// <summary>0 = evitar SDH; 1 = preferir SDH; 2 = tanto faz.</summary>
    [ObservableProperty]
    public partial int HearingImpairedIndex { get; set; }

    [ObservableProperty]
    public partial bool DownloadForcedSubtitles { get; set; }

    [ObservableProperty]
    public partial bool CheckEmbeddedSubtitles { get; set; }

    [ObservableProperty]
    public partial bool IncludeMachineTranslated { get; set; }

    [ObservableProperty]
    public partial bool HideSubtitleFiles { get; set; }

    public bool CanHideFiles => HiddenFile.IsSupported;

    public IReadOnlyList<string> LanguageModeOptions { get; } = ["Baixar todos os idiomas da lista", "Só o primeiro disponível (os seguintes são alternativa)"];

    public IReadOnlyList<string> HearingImpairedOptions { get; } = ["Evitar legendas para surdos (SDH)", "Preferir legendas SDH", "Tanto faz"];

    [ObservableProperty]
    public partial bool OpenSubtitlesEnabled { get; set; }

    [ObservableProperty]
    public partial string OpenSubtitlesApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string OpenSubtitlesUsername { get; set; } = "";

    [ObservableProperty]
    public partial string OpenSubtitlesPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? OpenSubtitlesStatus { get; private set; }

    [RelayCommand]
    private async Task TestOpenSubtitlesAsync()
    {
        var draft = new OpenSubtitlesSettings
        {
            Enabled = true,
            ApiKey = OpenSubtitlesApiKey.Trim(),
            Username = OpenSubtitlesUsername.Trim(),
            Password = OpenSubtitlesPassword,
        };
        if (string.IsNullOrEmpty(draft.ApiKey))
        {
            OpenSubtitlesStatus = "Informe a chave da API.";
            return;
        }

        OpenSubtitlesStatus = "Testando...";
        using var provider = new OpenSubtitlesProvider(httpFactory.CreateClient(OpenSubtitlesProvider.ProviderName), () => draft, () => false);
        OpenSubtitlesStatus = await TestProviderAsync(provider.TestAsync);
    }

    [RelayCommand]
    private Task OpenOpenSubtitlesAsync() => shell.OpenUrlAsync(OpenSubtitlesKeyUrl);

    public const string SubDlKeyUrl = "https://subdl.com/panel/api";

    [ObservableProperty]
    public partial bool SubDlEnabled { get; set; }

    [ObservableProperty]
    public partial string SubDlApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string? SubDlStatus { get; private set; }

    [RelayCommand]
    private async Task TestSubDlAsync()
    {
        var draft = new SubDlSettings { Enabled = true, ApiKey = SubDlApiKey.Trim() };
        if (string.IsNullOrEmpty(draft.ApiKey))
        {
            SubDlStatus = "Informe a chave da API.";
            return;
        }

        SubDlStatus = "Testando...";
        var provider = new SubDlProvider(httpFactory.CreateClient(SubDlProvider.ProviderName), () => draft);
        SubDlStatus = await TestProviderAsync(provider.TestAsync);
    }

    [RelayCommand]
    private Task OpenSubDlAsync() => shell.OpenUrlAsync(SubDlKeyUrl);

    public const string SubSourceUrl = "https://subsource.net";

    [ObservableProperty]
    public partial bool SubSourceEnabled { get; set; }

    [ObservableProperty]
    public partial string SubSourceApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string? SubSourceStatus { get; private set; }

    [RelayCommand]
    private async Task TestSubSourceAsync()
    {
        var draft = new SubSourceSettings { Enabled = true, ApiKey = SubSourceApiKey.Trim() };
        if (string.IsNullOrEmpty(draft.ApiKey))
        {
            SubSourceStatus = "Informe a chave da API.";
            return;
        }

        SubSourceStatus = "Testando...";
        var provider = new SubSourceProvider(httpFactory.CreateClient(SubSourceProvider.ProviderName), () => draft);
        SubSourceStatus = await TestProviderAsync(provider.TestAsync);
    }

    [RelayCommand]
    private Task OpenSubSourceAsync() => shell.OpenUrlAsync(SubSourceUrl);

    private static async Task<string> TestProviderAsync(Func<CancellationToken, Task<string>> test)
    {
        try
        {
            return await test(CancellationToken.None);
        }
        catch (SubtitleProviderException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException)
        {
            return $"Sem conexão: {e.Message}";
        }
    }

    [ObservableProperty]
    public partial string ApiKey { get; set; } = "";

    [ObservableProperty]
    public partial bool RenameSubtitles { get; set; }

    [ObservableProperty]
    public partial bool IncludeSpecials { get; set; }

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
        IncludeSpecials = s.IncludeSpecials;
        IgnoredFolders = string.Join(Environment.NewLine, s.IgnoredFolders);
        JunkPatterns = string.Join(Environment.NewLine, s.JunkPatterns);
        ProcessedCount = processed.Count;
        SubtitleLanguages = string.Join(", ", s.SubtitleLanguages);
        LanguageModeIndex = (int)s.SubtitleLanguageMode;
        HearingImpairedIndex = (int)s.SubtitleHearingImpaired;
        DownloadForcedSubtitles = s.DownloadForcedSubtitles;
        CheckEmbeddedSubtitles = s.CheckEmbeddedSubtitles;
        IncludeMachineTranslated = s.IncludeMachineTranslated;
        HideSubtitleFiles = s.HideSubtitleFiles;
        OpenSubtitlesEnabled = s.OpenSubtitles.Enabled;
        OpenSubtitlesApiKey = s.OpenSubtitles.ApiKey ?? "";
        OpenSubtitlesUsername = s.OpenSubtitles.Username ?? "";
        OpenSubtitlesPassword = s.OpenSubtitles.Password ?? "";
        OpenSubtitlesStatus = null;
        SubDlEnabled = s.SubDl.Enabled;
        SubDlApiKey = s.SubDl.ApiKey ?? "";
        SubDlStatus = null;
        SubSourceEnabled = s.SubSource.Enabled;
        SubSourceApiKey = s.SubSource.ApiKey ?? "";
        SubSourceStatus = null;
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
            s.IncludeSpecials = IncludeSpecials;
            s.IgnoredFolders = SplitLines(IgnoredFolders);
            s.JunkPatterns = SplitLines(JunkPatterns);
            s.SubtitleLanguages = [.. Core.Subtitles.SubtitleLanguages.NormalizeList(SplitLines(SubtitleLanguages))];
            s.SubtitleLanguageMode = (SubtitleLanguageMode)Math.Clamp(LanguageModeIndex, 0, 1);
            s.SubtitleHearingImpaired = (HearingImpairedPreference)Math.Clamp(HearingImpairedIndex, 0, 2);
            s.DownloadForcedSubtitles = DownloadForcedSubtitles;
            s.CheckEmbeddedSubtitles = CheckEmbeddedSubtitles;
            s.IncludeMachineTranslated = IncludeMachineTranslated;
            s.HideSubtitleFiles = HideSubtitleFiles;
            s.OpenSubtitles = new OpenSubtitlesSettings
            {
                Enabled = OpenSubtitlesEnabled,
                ApiKey = Blank(OpenSubtitlesApiKey),
                Username = Blank(OpenSubtitlesUsername),
                Password = string.IsNullOrEmpty(OpenSubtitlesPassword) ? null : OpenSubtitlesPassword,
            };
            s.SubDl = new SubDlSettings { Enabled = SubDlEnabled, ApiKey = Blank(SubDlApiKey) };
            s.SubSource = new SubSourceSettings { Enabled = SubSourceEnabled, ApiKey = Blank(SubSourceApiKey) };
        });
        subtitleProviders.ResetDisabled();
        SubtitleLanguages = string.Join(", ", settings.Current.SubtitleLanguages);
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

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
