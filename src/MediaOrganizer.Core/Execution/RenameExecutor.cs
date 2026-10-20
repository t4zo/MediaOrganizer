using MediaOrganizer.Core.Planning;

namespace MediaOrganizer.Core.Execution;

/// <summary>Resultado de aplicar ou desfazer um lote.</summary>
public sealed record ExecutionResult(RenameJournal Journal, int Succeeded, int Failed, IReadOnlyList<string> Errors);

/// <summary>
/// Aplica renomeações registrando tudo num journal <b>antes</b> de mexer nos arquivos,
/// para que qualquer lote possa ser desfeito, mesmo após uma falha no meio.
/// </summary>
public sealed class RenameExecutor(HistoryStore history)
{
    private const int SaveEvery = 25;

    public ExecutionResult Apply(string libraryPath, MediaKind kind, string description, IEnumerable<RenameItem> items,
        IEnumerable<string> folderKeys, IProgress<double>? progress = null)
    {
        // Arquivos primeiro; pastas depois (os arquivos ainda estão nos caminhos antigos).
        var ordered = items.Where(i => i.IsChange).OrderBy(i => i.Kind == ItemKind.Folder).ToList();

        var journal = new RenameJournal
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            CreatedAt = DateTimeOffset.Now,
            LibraryPath = libraryPath,
            Kind = kind,
            Description = description,
            FolderKeys = [.. folderKeys],
            Operations = [.. ordered.Select(i => new JournalOperation { From = i.SourcePath, To = i.TargetPath, IsDirectory = i.Kind == ItemKind.Folder })],
        };
        history.Save(journal);

        return Run(journal, journal.Operations, forward: true, progress);
    }

    public ExecutionResult Undo(RenameJournal journal, IProgress<double>? progress = null)
    {
        var operations = journal.Operations.Where(o => o.State == OperationState.Done).Reverse().ToList();
        var result = Run(journal, operations, forward: false, progress);
        journal.UndoneAt = DateTimeOffset.Now;
        history.Save(journal);
        return result;
    }

    private ExecutionResult Run(RenameJournal journal, List<JournalOperation> operations, bool forward, IProgress<double>? progress)
    {
        var errors = new List<string>();
        var succeeded = 0;

        for (var index = 0; index < operations.Count; index++)
        {
            var op = operations[index];
            var (from, to) = forward ? (op.From, op.To) : (op.To, op.From);
            try
            {
                Move(from, to, op.IsDirectory);
                if (!forward) DeleteIfEmptyCreatedFolder(from, to);
                op.State = forward ? OperationState.Done : OperationState.Undone;
                op.Error = null;
                succeeded++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (forward) op.State = OperationState.Failed;
                op.Error = e.Message;
                errors.Add($"{Path.GetFileName(from)}: {e.Message}");
            }

            if (index % SaveEvery == 0) history.Save(journal);
            progress?.Report((index + 1) / (double)operations.Count);
        }

        history.Save(journal);
        return new ExecutionResult(journal, succeeded, errors.Count, errors);
    }

    internal static void Move(string from, string to, bool isDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        // Troca só de maiúsculas/minúsculas: passa por um nome temporário (necessário em sistemas que não diferenciam).
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var temp = to + ".mo-tmp";
            MoveCore(from, temp, isDirectory);
            MoveCore(temp, to, isDirectory);
            return;
        }

        MoveCore(from, to, isDirectory);
    }

    /// <summary>Ao desfazer, remove a pasta criada para um filme solto se ela ficou vazia.</summary>
    private static void DeleteIfEmptyCreatedFolder(string movedFrom, string movedTo)
    {
        var createdDir = Path.GetDirectoryName(movedFrom);
        if (createdDir is null || string.Equals(createdDir, Path.GetDirectoryName(movedTo), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(createdDir) && !Directory.EnumerateFileSystemEntries(createdDir).Any()) Directory.Delete(createdDir);
    }

    private static void MoveCore(string from, string to, bool isDirectory)
    {
        if (isDirectory)
        {
            if (Directory.Exists(to)) throw new IOException("Já existe uma pasta com este nome.");
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }
    }
}
