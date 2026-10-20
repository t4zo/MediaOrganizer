namespace MediaOrganizer.Core.Metadata;

/// <summary>
/// Decide em qual idioma buscar títulos: o idioma original quando ele usa alfabeto latino,
/// senão inglês. Nunca usa traduções para outros idiomas.
/// </summary>
public static partial class LanguagePolicy
{
    public const string English = "en-US";

    /// <summary>Idiomas (ISO 639-1) escritos em alfabeto latino.</summary>
    public static readonly FrozenSet<string> LatinScriptLanguages = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "af", "ca", "cs", "cy", "da", "de", "en", "eo", "es", "et", "eu", "fi", "fo", "fr", "ga", "gl",
        "hr", "hu", "id", "is", "it", "la", "lb", "lt", "lv", "ms", "mt", "nb", "nl", "nn", "no", "pl",
        "pt", "ro", "sk", "sl", "sq", "sv", "sw", "tl", "tr", "vi", "bs");

    public static bool UsesOriginal(string? originalLanguage) =>
        originalLanguage is not null && LatinScriptLanguages.Contains(originalLanguage);

    /// <summary>Idioma a usar nas chamadas ao TMDb para uma obra com o idioma original informado.</summary>
    public static string TitleLanguage(string? originalLanguage) =>
        UsesOriginal(originalLanguage) && !originalLanguage!.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? originalLanguage.ToLowerInvariant()
            : English;

    /// <summary>Título genérico que o TMDb usa quando não há tradução ("Episode 5", "Folge 5").</summary>
    public static bool IsGenericEpisodeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || GenericTitleRegex().IsMatch(title);

    [GeneratedRegex(@"^(episode|episódio|episodio|épisode|folge|aflevering|afl\.?|odcinek|avsnitt|jakso|bölüm|puntata|capítulo|capitulo|epizoda|epizód|jakso|afsnit|episodi)\s*\d+$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GenericTitleRegex();
}
