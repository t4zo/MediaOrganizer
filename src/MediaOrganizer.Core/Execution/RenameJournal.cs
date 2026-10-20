using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Execution;

public enum OperationState
{
    Pending,
    Done,
    Failed,
    Undone,
}

/// <summary>Uma operação registrada no histórico.</summary>
public sealed class JournalOperation
{
    public required string From { get; init; }
    public required string To { get; init; }
    public bool IsDirectory { get; init; }
    public OperationState State { get; set; } = OperationState.Pending;
    public string? Error { get; set; }
}

/// <summary>Um lote de renomeações aplicado de uma vez (arquivo em <c>data/history</c>).</summary>
public sealed class RenameJournal
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string LibraryPath { get; init; }
    public required MediaKind Kind { get; init; }
    public required string Description { get; init; }

    /// <summary>Chaves das pastas afetadas (para invalidar o fingerprint ao desfazer).</summary>
    public List<string> FolderKeys { get; init; } = [];

    public List<JournalOperation> Operations { get; init; } = [];

    public DateTimeOffset? UndoneAt { get; set; }

    [JsonIgnore]
    public int DoneCount => Operations.Count(o => o.State == OperationState.Done);
    [JsonIgnore]
    public int FailedCount => Operations.Count(o => o.State == OperationState.Failed);
    [JsonIgnore]
    public bool CanUndo => UndoneAt is null && DoneCount > 0;
}
