using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Execution;

/// <summary>Persiste os lotes de renomeação em <c>data/history/*.json</c>.</summary>
public sealed class HistoryStore(DataPaths paths)
{
    public void Save(RenameJournal journal) =>
        JsonStore.Save(Path.Combine(paths.HistoryDirectory, journal.Id + ".json"), journal, CoreJsonContext.Default.RenameJournal);

    /// <summary>Lotes do mais recente para o mais antigo.</summary>
    public IReadOnlyList<RenameJournal> List()
    {
        if (!Directory.Exists(paths.HistoryDirectory)) return [];

        return [.. Directory.EnumerateFiles(paths.HistoryDirectory, "*.json")
            .Select(file => JsonStore.Load<RenameJournal?>(file, CoreJsonContext.Default.RenameJournal!, () => null))
            .OfType<RenameJournal>()
            .OrderByDescending(j => j.CreatedAt)];
    }
}
