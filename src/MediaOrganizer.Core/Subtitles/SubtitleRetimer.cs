using System.Globalization;
using System.Text;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Correção de sincronia aplicada ao salvar: cada tempo vira t × <see cref="Scale"/> + <see cref="Offset"/>.</summary>
/// <param name="Offset">Quanto somar aos tempos (o <see cref="SyncResult.Offset"/> medido).</param>
/// <param name="Scale">Fator de taxa de quadros (1 = só desloca).</param>
public sealed record SubtitleAdjustment(TimeSpan Offset, double Scale = 1)
{
    public static SubtitleAdjustment From(SyncResult sync) => new(sync.Offset, sync.Scale);

    /// <summary>O tempo corrigido, em milissegundos inteiros e nunca negativo.</summary>
    public TimeSpan Apply(TimeSpan time) =>
        TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(time.TotalMilliseconds * Scale + Offset.TotalMilliseconds, MidpointRounding.AwayFromZero)));

    /// <summary>Para a nota do journal: "sincronia corrigida em +0,40 s" ou "sincronia corrigida (×1,0427, −1,20 s)".</summary>
    public string Note
    {
        get
        {
            var seconds = (Offset < TimeSpan.Zero ? "−" : "+") + Math.Abs(Offset.TotalSeconds).ToString("0.00", CultureInfo.CurrentCulture) + " s";
            return Scale == 1
                ? $"sincronia corrigida em {seconds}"
                : $"sincronia corrigida (×{Scale.ToString("0.0000", CultureInfo.CurrentCulture)}, {seconds})";
        }
    }
}

/// <summary>
/// Regrava os tempos das falas de SRT, VTT e ASS/SSA com um <see cref="SubtitleAdjustment"/>, sem mexer no texto,
/// nos estilos, nas quebras de linha nem no resto do arquivo.
/// </summary>
public static class SubtitleRetimer
{
    /// <summary>Recebe e devolve UTF-8 com BOM (como sai de <see cref="SubtitleText.ToUtf8"/>).</summary>
    public static byte[] Apply(byte[] content, SubtitleAdjustment adjustment) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Apply(SubtitleText.Decode(content), adjustment))];

    public static string Apply(string text, SubtitleAdjustment adjustment)
    {
        // Separando só por '\n', o '\r' fica no fim de cada linha e o Join devolve as quebras originais.
        var lines = text.Split('\n');
        if (SubtitleCues.IsAss(lines)) RetimeAss(lines, adjustment);
        else RetimeSrt(lines, adjustment);
        return string.Join('\n', lines);
    }

    /// <summary>SRT e VTT: troca só os dois tempos da linha "início --> fim" (as configurações de posição do VTT ficam).</summary>
    private static void RetimeSrt(string[] lines, SubtitleAdjustment adjustment)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SubtitleCues.SrtTimingRegex().Match(lines[i]);
            if (!match.Success) continue;

            Group start = match.Groups["start"], end = match.Groups["end"];
            if (SubtitleCues.ParseSrtTime(start.Value) is not { } s || SubtitleCues.ParseSrtTime(end.Value) is not { } e) continue;

            var line = lines[i];
            lines[i] = line[..start.Index] + FormatSrt(adjustment.Apply(s), start.Value) +
                       line[(start.Index + start.Length)..end.Index] + FormatSrt(adjustment.Apply(e), end.Value) +
                       line[(end.Index + end.Length)..];
        }
    }

    /// <summary>ASS/SSA: colunas Start e End (pela linha "Format:") das linhas "Dialogue:" e "Comment:".</summary>
    private static void RetimeAss(string[] lines, SubtitleAdjustment adjustment)
    {
        int startField = 1, endField = 2, textField = 9;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = trimmed["Format:".Length..].Split(',').Select(f => f.Trim()).ToList();
                var start = fields.FindIndex(f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
                var end = fields.FindIndex(f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
                var text = fields.FindIndex(f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
                if (start >= 0 && end >= 0 && text >= 0) (startField, endField, textField) = (start, end, text);
                continue;
            }
            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                continue;

            // O texto é a última coluna e pode ter vírgulas: com o limite do Split, ele volta inteiro.
            var colon = line.IndexOf(':') + 1;
            var parts = line[colon..].Split(',', textField + 1);
            if (parts.Length <= Math.Max(startField, endField)) continue;
            if (SubtitleCues.ParseAssTime(parts[startField]) is not { } s || SubtitleCues.ParseAssTime(parts[endField]) is not { } e) continue;

            parts[startField] = Replace(parts[startField], FormatAss(adjustment.Apply(s)));
            parts[endField] = Replace(parts[endField], FormatAss(adjustment.Apply(e)));
            lines[i] = line[..colon] + string.Join(',', parts);
        }
    }

    /// <summary>Mantém o separador (vírgula no SRT, ponto no VTT) e omite as horas só se o original omitia e não passa de 1 h.</summary>
    private static string FormatSrt(TimeSpan time, string original)
    {
        var separator = original.Contains(',', StringComparison.Ordinal) ? ',' : '.';
        var clock = $"{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
        return original.Count(c => c == ':') == 2 || time.TotalHours >= 1 ? $"{(int)time.TotalHours:00}:{clock}" : clock;
    }

    /// <summary>"0:01:02.34" (centésimos; horas sem zero à esquerda, como o Aegisub grava).</summary>
    private static string FormatAss(TimeSpan time)
    {
        var centiseconds = (long)Math.Round(time.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"{centiseconds / 360_000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}");
    }

    /// <summary>Troca o valor da coluna preservando os espaços em volta.</summary>
    private static string Replace(string field, string value) =>
        field[..(field.Length - field.TrimStart().Length)] + value + field[field.TrimEnd().Length..];
}
