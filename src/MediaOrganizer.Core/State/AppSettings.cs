using System.Text.Json.Serialization;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.State;

/// <summary>Credenciais do OpenSubtitles.com. O login é opcional, mas aumenta a cota diária de downloads.</summary>
public sealed class OpenSubtitlesSettings
{
    public bool Enabled { get; set; } = true;
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// Opções do OpenSubtitles baixadas a cada clique em "Verificar sincronia" (a cota dele é pequena).
    /// SubDL e SubSource não têm esse limite: todas as opções deles são verificadas.
    /// </summary>
    public int SyncCheckLimit { get; set; } = SubtitleSyncChecker.DefaultOpenSubtitlesLimit;
}

public sealed class SubDlSettings
{
    public bool Enabled { get; set; } = true;
    public string? ApiKey { get; set; }
}

public sealed class SubSourceSettings
{
    public bool Enabled { get; set; } = true;
    public string? ApiKey { get; set; }
}

/// <summary>Configurações do usuário, salvas em <c>data/settings.json</c>.</summary>
public sealed class AppSettings
{
    public string? TmdbApiKey { get; set; }

    public string? LastLibraryPath { get; set; }

    public MediaKind LastKind { get; set; } = MediaKind.Series;

    public bool RenameSubtitles { get; set; } = true;

    /// <summary>
    /// Varre as pastas de especiais ("Specials", "Season 00") e usa a temporada 0 do TMDb.
    /// Prevalece sobre <see cref="IgnoredFolders"/> (configurações da v1.0 ainda listam "Specials").
    /// </summary>
    public bool IncludeSpecials { get; set; } = true;

    /// <summary>Pastas ignoradas em qualquer nível (extras, making-of, audiobooks...).</summary>
    public List<string> IgnoredFolders { get; set; } =
    [
        "Extras", "Featurettes", "Behind The Scenes", "Deleted Scenes", "Interviews",
        "Trailers", "Bonus", "AudioBooks", "Sample", "Samples",
    ];

    /// <summary>Padrões (curinga * e ?) de arquivos de lixo de release.</summary>
    public List<string> JunkPatterns { get; set; } =
    [
        "*.nfo", "*.url", "*.website", "*.exe", "*rarbg*.txt", "*psarips*.txt", "www.*.txt",
        "*torrent*.txt", "*.torrent", "sample*.mkv", "sample*.mp4", "*-sample.*", "Thumbs.db",
    ];

    /// <summary>Idiomas de legenda desejados, em ordem de prioridade.</summary>
    public List<string> SubtitleLanguages { get; set; } = [SubtitleLanguage.PortugueseBrazil, SubtitleLanguage.English];

    public SubtitleLanguageMode SubtitleLanguageMode { get; set; } = SubtitleLanguageMode.All;

    /// <summary>Também buscar legendas forçadas (só as falas em outro idioma), além da normal.</summary>
    public bool DownloadForcedSubtitles { get; set; }

    public HearingImpairedPreference SubtitleHearingImpaired { get; set; } = HearingImpairedPreference.Avoid;

    /// <summary>Faixas de legenda embutidas no MKV contam como "já tem".</summary>
    public bool CheckEmbeddedSubtitles { get; set; } = true;

    /// <summary>Incluir legendas traduzidas por IA ou tradução automática.</summary>
    public bool IncludeMachineTranslated { get; set; }

    /// <summary>Legendas baixadas para a pasta do vídeo recebem o atributo oculto (Windows/macOS).</summary>
    public bool HideSubtitleFiles { get; set; } = true;

    public SubtitleOutput SubtitleOutput { get; set; } = SubtitleOutput.File;

    /// <summary>Caminho do mkvmerge; vazio = procurar no PATH e nos locais padrão.</summary>
    public string? MkvMergePath { get; set; }

    /// <summary>Guarda o vídeo original ao embutir, para desfazer sem novo remux.</summary>
    public bool KeepOriginalAfterEmbed { get; set; } = true;

    public OpenSubtitlesSettings OpenSubtitles { get; set; } = new();

    public SubDlSettings SubDl { get; set; } = new();

    public SubSourceSettings SubSource { get; set; } = new();

    /// <summary>Correspondências escolhidas: "Series/Dark" → id do TMDb.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> Matches { get; } = new(StringComparer.OrdinalIgnoreCase);
}
