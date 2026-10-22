using System.Globalization;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Um intervalo em que a legenda mostra texto.</summary>
public readonly record struct SubtitleCue(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Lê só os tempos das falas de SRT, VTT e ASS/SSA (o texto não importa para a sincronia).</summary>
public static partial class SubtitleCues
{
    /// <summary>Falas mais longas que isso são lixo (créditos que ficam a tela toda, erros de digitação no tempo).</summary>
    private static readonly TimeSpan MaxCueDuration = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<SubtitleCue> Parse(byte[] content) => Parse(SubtitleText.Decode(content));

    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var cues = IsAss(lines) ? ParseAss(lines) : ParseSrt(lines);
        return [.. cues.Where(c => c.End > c.Start && c.Duration <= MaxCueDuration).OrderBy(c => c.Start)];
    }

    private static bool IsAss(string[] lines) =>
        lines.Any(l => l.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) ||
                       l.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase));

    /// <summary>SRT e VTT: linha "início --> fim" seguida do texto até a linha em branco; fala sem texto não conta.</summary>
    private static List<SubtitleCue> ParseSrt(string[] lines)
    {
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SrtTimingRegex().Match(lines[i]);
            if (!match.Success) continue;

            var hasText = false;
            for (var j = i + 1; j < lines.Length && lines[j].Trim().Length > 0 && !SrtTimingRegex().IsMatch(lines[j]); j++)
                hasText |= StripTags(lines[j]).Length > 0;

            if (hasText && ParseSrtTime(match.Groups["start"].Value) is { } start && ParseSrtTime(match.Groups["end"].Value) is { } end)
                cues.Add(new SubtitleCue(start, end));
        }
        return cues;
    }

    /// <summary>ASS/SSA: linhas "Dialogue:" da seção [Events], com as colunas da linha "Format:".</summary>
    private static List<SubtitleCue> ParseAss(string[] lines)
    {
        var cues = new List<SubtitleCue>();
        int startField = 1, endField = 2, textField = 9;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line["Format:".Length..].Split(',').Select(f => f.Trim()).ToList();
                var start = fields.FindIndex(f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
                var end = fields.FindIndex(f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
                var text = fields.FindIndex(f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
                if (start >= 0 && end >= 0 && text >= 0) (startField, endField, textField) = (start, end, text);
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            // O texto é a última coluna e pode ter vírgulas.
            var parts = line["Dialogue:".Length..].Split(',', textField + 1);
            if (parts.Length <= textField) continue;
            if (StripTags(parts[textField].Replace("\\N", " ", StringComparison.OrdinalIgnoreCase)).Length == 0) continue;

            if (ParseAssTime(parts[startField]) is { } s && ParseAssTime(parts[endField]) is { } e) cues.Add(new SubtitleCue(s, e));
        }
        return cues;
    }

    private static string StripTags(string text) => TagRegex().Replace(text, "").Trim();

    /// <summary>"01:02:03,456", "01:02:03.456" ou "02:03.456" (VTT sem horas).</summary>
    private static TimeSpan? ParseSrtTime(string value)
    {
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        if (Seconds(parts[^1]) is not { } seconds) return null;
        return new TimeSpan(0, hours, minutes, 0) + seconds;
    }

    /// <summary>"0:01:02.34" (centésimos).</summary>
    private static TimeSpan? ParseAssTime(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            Seconds(parts[2]) is not { } seconds)
            return null;
        return new TimeSpan(0, hours, minutes, 0) + seconds;
    }

    /// <summary>"03.450" ou "03.45" (centésimos) em milissegundos exatos, sem arredondar como double.</summary>
    private static TimeSpan? Seconds(string value)
    {
        var parts = value.Split('.');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        var fraction = parts.Length == 2 ? parts[1].PadRight(3, '0')[..3] : "000";
        if (!int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)) return null;
        return TimeSpan.FromMilliseconds(seconds * 1000L + milliseconds);
    }

    [GeneratedRegex(@"^\s*(?<start>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex SrtTimingRegex();

    /// <summary>Tags HTML do SRT/VTT e blocos de estilo do ASS ("{\an8}").</summary>
    [GeneratedRegex(@"<[^>]*>|\{[^}]*\}")]
    private static partial Regex TagRegex();
}
