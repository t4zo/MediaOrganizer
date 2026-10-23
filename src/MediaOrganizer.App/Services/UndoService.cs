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

        var done = journal.Operations.Where(o => o.State == OperationState.Done).ToList();
        var created = done.Count(o => o.Kind == OperationKind.Create);
        var embedded = done.Count(o => o.Kind == OperationKind.Embed);
        var moved = done.Count - created - embedded;
        var effects = new List<string>();
        if (moved > 0) effects.Add($"{moved} arquivo(s) voltarão aos nomes anteriores");
        if (created > 0) effects.Add($"{created} legenda(s) baixada(s) irão para a Lixeira");
        if (embedded > 0) effects.Add($"{embedded} vídeo(s) voltarão a ficar sem as legendas embutidas");

        if (confirm && !await shell.ConfirmAsync(moved > 0 && created + embedded == 0 ? "Desfazer renomeação?" : "Desfazer lote?",
                $"{string.Join("; ", effects)}.\n\"{journal.Description}\"",
                "Desfazer"))
            return false;

        var result = await Task.Run(() => executor.Undo(journal));
        analyzer.ForgetProcessed(journal.FolderKeys);

        if (result.Failed == 0)
            shell.ShowToast(created + embedded == 0
                ? $"Desfeito: {result.Succeeded} arquivo(s) voltaram ao nome anterior."
                : $"Desfeito: {result.Succeeded} operação(ões).");
        else
            shell.ShowToast($"Desfeito com {result.Failed} erro(s). Primeiro erro: {result.Errors[0]}", isError: true);

        Undone?.Invoke(journal);
        return true;
    }
}
