using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.State;

/// <summary>Registro de uma pasta já processada (ver <see cref="Scanning.FolderFingerprint"/>).</summary>
public sealed record ProcessedEntry(string Hash, int TmdbId, DateTimeOffset ProcessedAt);

/// <summary>Conteúdo de <c>data/processed.json</c>.</summary>
public sealed class ProcessedState
{
    public int Version { get; set; } = 1;

    /// <summary>Chave: "&lt;kind&gt;/&lt;nome da raiz&gt;/&lt;pasta&gt;" (ex.: "Series/Series/Dark"), portátil entre sistemas.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, ProcessedEntry> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
}
