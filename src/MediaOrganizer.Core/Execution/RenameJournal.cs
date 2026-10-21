using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Execution;

public enum OperationState
{
    Pending,
    Done,
    Failed,
    Undone,
}

public enum OperationKind
{
    /// <summary>Renomear/mover <c>From</c> → <c>To</c>; desfazer move de volta.</summary>
    Move,
    /// <summary>Arquivo criado em <c>To</c> (legenda baixada; <c>From</c> descreve a origem); desfazer manda para a Lixeira.</summary>
    Create,
    /// <summary>
    /// Faixas embutidas no vídeo <c>To</c>. <c>From</c> é o backup do original (vazio se não guardado);
    /// desfazer restaura o backup ou remove as faixas <see cref="JournalOperation.AddedTrackIds"/>.
    /// </summary>
    Embed,
}

/// <summary>Uma operação registrada no histórico.</summary>
public sealed class JournalOperation
{
    public OperationKind Kind { get; init; }
    public required string From { get; set; }
    public required string To { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>Texto exibido no Histórico (ex.: idiomas embutidos).</summary>
    public string? Note { get; set; }

    /// <summary>IDs (mkvmerge) das faixas adicionadas por <see cref="OperationKind.Embed"/>.</summary>
    public List<int>? AddedTrackIds { get; set; }
    public OperationState State { get; set; } = OperationState.Pending;
    public string? Error { get; set; }
}

/// <summary>Um lote de renomeações aplicado de uma vez (arquivo em <c>data/history</c>).</summary>
public sealed class RenameJournal
{
    /// <summary>"yyyyMMdd-HHmmss-xxxxxx".</summary>
    public static string NewId() => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

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
