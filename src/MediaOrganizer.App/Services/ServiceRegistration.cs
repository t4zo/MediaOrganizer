using MediaOrganizer.App.ViewModels;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Collection;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;
using MediaOrganizer.Core.Subtitles.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace MediaOrganizer.App.Services;

internal static class ServiceRegistration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var paths = DataPaths.Resolve();

        services.AddSingleton(paths);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ProcessedStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton(new MetadataCache(paths.CacheDirectory, MetadataCache.DefaultTimeToLive));

        // Retentativas com backoff (inclui HTTP 429 do TMDb) via Microsoft.Extensions.Http.Resilience.
        services.AddHttpClient(nameof(TmdbClient), http =>
            {
                http.BaseAddress = TmdbClient.BaseAddress;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaOrganizer/1.0");
            })
            .AddStandardResilienceHandler();
        services.AddHttpClient(nameof(PosterLoader)).AddStandardResilienceHandler();

        services.AddSingleton(sp => new TmdbClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TmdbClient)),
            () => sp.GetRequiredService<SettingsStore>().Current.TmdbApiKey,
            sp.GetRequiredService<MetadataCache>()));
        services.AddSingleton(sp => new PosterLoader(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PosterLoader))));

        // Sites de legenda: sem o handler de resiliência padrão, porque nesses sites um 429/406 pode ser cota esgotada
        // (ProviderHttp repete só falhas de rede/5xx e 429 com Retry-After curto).
        var subtitleCache = new MetadataCache(Path.Combine(paths.CacheDirectory, "subtitles"), TimeSpan.FromDays(1));
        services.AddHttpClient(OpenSubtitlesProvider.ProviderName);
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new OpenSubtitlesProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenSubtitlesProvider.ProviderName),
                () => settings.Current.OpenSubtitles, () => settings.Current.IncludeMachineTranslated, subtitleCache);
        });
        services.AddHttpClient(SubDlProvider.ProviderName);
        services.AddSingleton(sp => new SubDlProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(SubDlProvider.ProviderName),
            () => sp.GetRequiredService<SettingsStore>().Current.SubDl, subtitleCache));
        services.AddHttpClient(SubSourceProvider.ProviderName);
        services.AddSingleton(sp => new SubSourceProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(SubSourceProvider.ProviderName),
            () => sp.GetRequiredService<SettingsStore>().Current.SubSource, subtitleCache));
        services.AddSingleton(sp => new SubtitleProviderSet(
            [sp.GetRequiredService<OpenSubtitlesProvider>(), sp.GetRequiredService<SubDlProvider>(), sp.GetRequiredService<SubSourceProvider>()],
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMkvMergeRunner, MkvMergeRunner>();
        services.AddSingleton(sp => new SubtitleEmbedder(
            sp.GetRequiredService<IMkvMergeRunner>(), () => sp.GetRequiredService<SettingsStore>().Current.MkvMergePath));
        services.AddSingleton<SubtitleAnalyzer>();
        services.AddSingleton<SubtitleDownloader>();

        services.AddSingleton<MediaMatcher>();
        services.AddSingleton<RenamePlanner>();
        services.AddSingleton<LibraryAnalyzer>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CollectionAnalyzer>();
        services.AddSingleton(sp => new RenameExecutor(sp.GetRequiredService<HistoryStore>(), embedUndo: sp.GetRequiredService<SubtitleEmbedder>()));

        services.AddSingleton<ShellServices>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<CollectionViewModel>();
        services.AddSingleton<SubtitlesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();

        return services.BuildServiceProvider();
    }
}
