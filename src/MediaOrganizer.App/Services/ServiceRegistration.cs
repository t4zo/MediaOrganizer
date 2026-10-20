using MediaOrganizer.App.ViewModels;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.State;
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

        services.AddSingleton<MediaMatcher>();
        services.AddSingleton<RenamePlanner>();
        services.AddSingleton<LibraryAnalyzer>();
        services.AddSingleton<RenameExecutor>();

        services.AddSingleton<ShellServices>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();

        return services.BuildServiceProvider();
    }
}
