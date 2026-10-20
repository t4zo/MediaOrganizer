using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.State;

/// <summary>Guarda os fingerprints das pastas processadas em <c>data/processed.json</c>.</summary>
public sealed class ProcessedStore(DataPaths paths)
{
    private readonly Lock _lock = new();
    private readonly ProcessedState _state = JsonStore.Load(paths.ProcessedFile, CoreJsonContext.Default.ProcessedState, () => new ProcessedState());

    public ProcessedEntry? Get(string key)
    {
        lock (_lock)
        {
            return _state.Folders.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    /// <summary>A pasta está exatamente como foi deixada no último processamento?</summary>
    public bool IsUnchanged(LibraryFolder folder) =>
        Get(folder.Key) is { } entry && FolderFingerprint.Compute(folder, entry.TmdbId) == entry.Hash;

    public void MarkProcessed(LibraryFolder folder, int tmdbId)
    {
        var entry = new ProcessedEntry(FolderFingerprint.Compute(folder, tmdbId), tmdbId, DateTimeOffset.Now);
        lock (_lock)
        {
            _state.Folders[folder.Key] = entry;
            Save();
        }
    }

    public void Remove(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            foreach (var key in keys) _state.Folders.Remove(key);
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _state.Folders.Clear();
            Save();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _state.Folders.Count;
        }
    }

    private void Save() => JsonStore.Save(paths.ProcessedFile, _state, CoreJsonContext.Default.ProcessedState);
}
