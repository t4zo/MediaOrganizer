using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.State;

/// <summary>Configurações do usuário, salvas em <c>data/settings.json</c>.</summary>
public sealed class AppSettings
{
    public string? TmdbApiKey { get; set; }

    public string? LastLibraryPath { get; set; }

    public MediaKind LastKind { get; set; } = MediaKind.Series;

    public bool RenameSubtitles { get; set; } = true;

    /// <summary>Pastas ignoradas em qualquer nível (extras, making-of, audiobooks...).</summary>
    public List<string> IgnoredFolders { get; set; } =
    [
        "Extras", "Featurettes", "Behind The Scenes", "Deleted Scenes", "Interviews",
        "Trailers", "Bonus", "Specials", "AudioBooks", "Sample", "Samples",
    ];

    /// <summary>Padrões (curinga * e ?) de arquivos de lixo de release.</summary>
    public List<string> JunkPatterns { get; set; } =
    [
        "*.nfo", "*.url", "*.website", "*.exe", "*rarbg*.txt", "*psarips*.txt", "www.*.txt",
        "*torrent*.txt", "*.torrent", "sample*.mkv", "sample*.mp4", "*-sample.*", "Thumbs.db",
    ];

    /// <summary>Correspondências escolhidas: "Series/Dark" → id do TMDb.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> Matches { get; } = new(StringComparer.OrdinalIgnoreCase);
}
