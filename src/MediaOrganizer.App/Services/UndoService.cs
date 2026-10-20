using MediaOrganizer.Core;
using MediaOrganizer.Core.Execution;

namespace MediaOrganizer.App.Services;

/// <summary>Desfaz um lote (a partir do histórico ou do toast "Desfazer").</summary>
public sealed class UndoService(ShellServices shell, RenameExecutor executor, LibraryAnalyzer analyzer)
{
    /// <summary>Disparado após desfazer, para a tela de organização reanalisar.</summary>
    public event Action<RenameJournal>? Undone;

    public async Task<bool> UndoAsync(RenameJournal journal, bool confirm = true)
    {
        if (!journal.CanUndo) return false;

        if (confirm && !await shell.ConfirmAsync("Desfazer renomeação?",
                $"{journal.DoneCount} arquivo(s) voltarão aos nomes anteriores.\n\"{journal.Description}\"",
                "Desfazer"))
            return false;

        var result = await Task.Run(() => executor.Undo(journal));
        analyzer.ForgetProcessed(journal.FolderKeys);

        if (result.Failed == 0)
            shell.ShowToast($"Desfeito: {result.Succeeded} arquivo(s) voltaram ao nome anterior.");
        else
            shell.ShowToast($"Desfeito com {result.Failed} erro(s). Primeiro erro: {result.Errors[0]}", isError: true);

        Undone?.Invoke(journal);
        return true;
    }
}
