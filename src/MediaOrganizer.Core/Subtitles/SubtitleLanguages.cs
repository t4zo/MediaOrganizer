namespace MediaOrganizer.Core.Subtitles;

/// <summary>Nomes e normalização dos códigos de idioma das legendas.</summary>
public static class SubtitleLanguages
{
    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["pt-BR"] = "Português (Brasil)", ["pt"] = "Português", ["en"] = "English", ["es"] = "Español",
        ["fr"] = "Français", ["de"] = "Deutsch", ["it"] = "Italiano", ["ja"] = "日本語", ["ko"] = "한국어",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(string code) => Names.TryGetValue(code, out var name) ? name : code;

    /// <summary>"pt-br", "PT_BR", "pob" → "pt-BR"; "EN", "eng" → "en". Desconhecido: devolve aparado.</summary>
    public static string Normalize(string code)
    {
        var trimmed = code.Trim().Replace('_', '-');
        if (trimmed.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("pob", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("br-pt", StringComparison.OrdinalIgnoreCase))
            return SubtitleLanguage.PortugueseBrazil;
        if (Planning.SubtitleMatcher.LanguageCodeFor(trimmed) is { } known) return known;

        // Região: "en-US" → "en" (inglês não diferencia região); outras mantêm "xx-YY".
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && Planning.SubtitleMatcher.LanguageCodeFor(trimmed[..dash]) is { } language)
            return language == SubtitleLanguage.English ? language : $"{language}-{trimmed[(dash + 1)..].ToUpperInvariant()}";
        return trimmed;
    }

    /// <summary>Lista de idiomas das configurações, normalizada e sem repetições.</summary>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string> codes) =>
        [.. codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
}
