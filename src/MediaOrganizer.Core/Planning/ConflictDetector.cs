namespace MediaOrganizer.Core.Planning;

/// <summary>
/// Marca como conflito itens cujo destino já existe em disco ou coincide com o destino de outro item.
/// A comparação ignora maiúsculas/minúsculas porque Windows e macOS não diferenciam.
/// </summary>
public static class ConflictDetector
{
    public static IReadOnlyList<RenameItem> Apply(IReadOnlyList<RenameItem> items)
    {
        var duplicateTargets = items
            .Where(i => i.IsChange || i.Status == ItemStatus.Conflict)
            .GroupBy(i => i.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. items.Select(item => Check(item, duplicateTargets))];
    }

    private static RenameItem Check(RenameItem item, HashSet<string> duplicateTargets)
    {
        // Um item que já estava em conflito volta a ser avaliado (ex.: depois de o usuário editar o nome).
        if (item.Status == ItemStatus.Conflict) item = item.WithTarget(item.TargetPath);
        if (!item.IsChange) return item;

        if (duplicateTargets.Contains(item.TargetPath))
            return item with { Status = ItemStatus.Conflict, Message = "Outro arquivo ficaria com este mesmo nome." };

        var caseOnlyRename = RenameItem.PathsEqual(item.SourcePath, item.TargetPath, ignoreCase: true);
        var exists = item.Kind == ItemKind.Folder ? Directory.Exists(item.TargetPath) : File.Exists(item.TargetPath);
        if (exists && !caseOnlyRename)
            return item with { Status = ItemStatus.Conflict, Message = "Já existe um arquivo com este nome." };

        if (Path.GetFileName(item.TargetPath).Length == 0)
            return item with { Status = ItemStatus.Conflict, Message = "O nome não pode ficar vazio." };

        return item;
    }
}
