namespace MediaOrganizer.Core.Naming;

/// <summary>
/// Torna um título oficial seguro como nome de arquivo no Windows, macOS e Linux,
/// mantendo-o o mais fiel possível ao original.
/// </summary>
public static partial class FileNameSanitizer
{
    private static readonly FrozenSet<string> WindowsReservedNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9");

    public static string Sanitize(string name)
    {
        var text = DigitColonRegex().Replace(name, ".");          // 1:23:45 → 1.23.45
        text = ColonRegex().Replace(text, " - ");                  // "Monster: The Ed Gein Story" → "Monster - The Ed Gein Story"
        text = text.Replace(':', '-').Replace('/', '-').Replace('\\', '-').Replace('"', '\'');
        text = InvalidCharsRegex().Replace(text, "");
        text = WhitespaceRegex().Replace(text, " ").Trim().TrimEnd('.', ' ');

        return WindowsReservedNames.Contains(text) ? text + "_" : text;
    }

    [GeneratedRegex(@"(?<=\d):(?=\d)")]
    private static partial Regex DigitColonRegex();

    [GeneratedRegex(@"\s*:\s+")]
    private static partial Regex ColonRegex();

    [GeneratedRegex(@"[<>|?*\x00-\x1F]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
