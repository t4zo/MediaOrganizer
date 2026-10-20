namespace MediaOrganizer.Core.State;

/// <summary>Mantém as configurações em memória e as persiste em <c>data/settings.json</c>.</summary>
public sealed class SettingsStore(DataPaths paths)
{
    private readonly Lock _lock = new();

    public AppSettings Current { get; private set; } = JsonStore.Load(paths.SettingsFile, CoreJsonContext.Default.AppSettings, () => new AppSettings());

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            JsonStore.Save(paths.SettingsFile, Current, CoreJsonContext.Default.AppSettings);
        }
    }

    public int? GetMatch(string matchKey)
    {
        lock (_lock)
        {
            return Current.Matches.TryGetValue(matchKey, out var id) ? id : null;
        }
    }

    public void SetMatch(string matchKey, int tmdbId) => Update(s => s.Matches[matchKey] = tmdbId);
}
