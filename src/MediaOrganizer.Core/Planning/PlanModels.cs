using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Planning;

public enum ItemKind
{
    Video,
    Subtitle,
    Folder,
}

public enum ItemStatus
{
    /// <summary>Será renomeado.</summary>
    Ok,
    /// <summary>Já está no padrão.</summary>
    Unchanged,
    /// <summary>Será renomeado, mas com ressalva (ex.: título não encontrado no TMDb).</summary>
    Warning,
    /// <summary>Não foi possível determinar o novo nome; fica como está.</summary>
    Skipped,
    /// <summary>O novo nome colide com outro arquivo; bloqueado.</summary>
    Conflict,
}

/// <summary>Uma operação de renomeação planejada (caminhos absolutos).</summary>
/// <param name="NeedsConfirmation">
/// O destino é uma suposição (ex.: especial casado pelo título): o item começa desmarcado na prévia.
/// </param>
public sealed record RenameItem(
    ItemKind Kind, string SourcePath, string TargetPath, ItemStatus Status, string? Message = null, bool NeedsConfirmation = false)
{
    public bool IsChange => Status is ItemStatus.Ok or ItemStatus.Warning;

    /// <summary>Novo destino (ex.: editado pelo usuário): o status é recalculado e a ressalva é descartada.</summary>
    public RenameItem WithTarget(string targetPath) => this with
    {
        TargetPath = targetPath,
        Status = PathsEqual(SourcePath, targetPath, ignoreCase: false) ? ItemStatus.Unchanged : ItemStatus.Ok,
        Message = null,
        NeedsConfirmation = false,
    };

    internal static bool PathsEqual(string a, string b, bool ignoreCase) =>
        string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public enum FolderStatus
{
    /// <summary>Há arquivos a renomear.</summary>
    Changes,
    /// <summary>Tudo já está no padrão.</summary>
    NoChanges,
    /// <summary>Fingerprint igual ao último processamento: nada foi alterado.</summary>
    AlreadyProcessed,
    /// <summary>Correspondência incerta ou conflitos: precisa de revisão.</summary>
    NeedsReview,
    /// <summary>Não encontrado no TMDb.</summary>
    NotFound,
    /// <summary>Erro ao analisar (rede, permissão...).</summary>
    Error,
}

/// <summary>Resultado da análise de uma pasta.</summary>
public sealed record FolderPlan(
    LibraryFolder Folder,
    MediaCandidate? Match,
    bool LowConfidence,
    IReadOnlyList<RenameItem> Items,
    FolderStatus Status,
    string? Message = null)
{
    public int ChangeCount => Items.Count(i => i.IsChange);

    public static FolderPlan Failed(LibraryFolder folder, FolderStatus status, string message, MediaCandidate? match = null) =>
        new(folder, match, LowConfidence: false, [], status, message);

    /// <summary>Recalcula o status da pasta a partir dos itens.</summary>
    public static FolderStatus StatusFor(IReadOnlyList<RenameItem> items, bool lowConfidence) =>
        lowConfidence || items.Any(i => i.Status == ItemStatus.Conflict) ? FolderStatus.NeedsReview
        : items.Any(i => i.IsChange) ? FolderStatus.Changes
        : FolderStatus.NoChanges;
}
