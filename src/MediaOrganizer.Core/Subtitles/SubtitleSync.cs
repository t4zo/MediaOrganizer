using System.Globalization;

namespace MediaOrganizer.Core.Subtitles;

public enum SyncVerdict
{
    /// <summary>O site confirmou o hash do arquivo; não foi preciso baixar para comparar.</summary>
    Hash,
    /// <summary>As falas batem com a referência sem ajuste.</summary>
    Synced,
    /// <summary>Bate com a referência só com um deslocamento fixo (<see cref="SyncResult.Offset"/>).</summary>
    Shifted,
    /// <summary>Bate só esticando o tempo: feita para outra taxa de quadros (25 × 23,976).</summary>
    FrameRate,
    /// <summary>Não bate em nenhum alinhamento: outro corte, outro episódio ou legenda incompleta.</summary>
    Mismatch,
}

/// <param name="Precision">
/// Nota das falas da candidata sem nenhum ajuste (1 se começam a até 250 ms de uma fala da referência, caindo até
/// 0 com 1 s de diferença), em relação ao melhor alinhamento possível (0 a 1). Com <see cref="SyncVerdict.Mismatch"/>,
/// é a fração bruta.
/// </param>
/// <param name="Offset">Quanto somar aos tempos da candidata para alinhar (positivo: a legenda aparece cedo demais).</param>
/// <param name="Scale">Fator de tempo do melhor alinhamento (1 = mesma taxa de quadros).</param>
/// <param name="Confidence">Falas que começam junto com a referência no melhor alinhamento (o quanto as duas são "a mesma legenda").</param>
public sealed record SyncResult(double Precision, TimeSpan Offset, double Scale, SyncVerdict Verdict, double Confidence = 1)
{
    public static SyncResult HashMatch { get; } = new(1, TimeSpan.Zero, 1, SyncVerdict.Hash);

    public int Percent => (int)Math.Round(Precision * 100, MidpointRounding.AwayFromZero);

    public bool IsGood => Verdict is SyncVerdict.Hash or SyncVerdict.Synced;

    /// <summary>Ordem na lista: hash, sincronizadas, deslocadas, e por último as que não conferem.</summary>
    public double SortKey => Verdict switch
    {
        SyncVerdict.Hash => 3,
        SyncVerdict.Synced => 2 + Precision,
        SyncVerdict.Shifted or SyncVerdict.FrameRate => 1 + Precision,
        _ => Precision / 10,
    };

    /// <summary>Texto curto para o menu e o selo: "96% sincronizada", "12% · atrasada 2,3 s".</summary>
    public string Label => Verdict switch
    {
        SyncVerdict.Hash => "hash",
        SyncVerdict.Synced => HasSmallOffset ? $"{Percent}% sincronizada · {OffsetText}" : $"{Percent}% sincronizada",
        SyncVerdict.Shifted => $"{Percent}% · {OffsetText}",
        SyncVerdict.FrameRate => $"{Percent}% · outra taxa de quadros",
        _ => "não confere",
    };

    public string Description => Verdict switch
    {
        SyncVerdict.Hash => "O site confirmou que a legenda é deste arquivo (hash): sincronia praticamente garantida.",
        SyncVerdict.Synced => $"Nota {Percent}% (do máximo possível entre as duas): as falas começam junto com as da legenda de referência" +
                              (HasSmallOffset ? $", com {Seconds} de diferença; pode ser da própria referência, e costuma passar despercebido." : "."),
        SyncVerdict.Shifted => $"As falas aparecem {Seconds} {(Offset > TimeSpan.Zero ? "antes" : "depois")} da hora (comparando com a referência).",
        SyncVerdict.FrameRate => "Feita para outra taxa de quadros (ex.: 25 × 23,976 fps): o atraso cresce ao longo do episódio.",
        _ => "As falas não coincidem com a referência em nenhum alinhamento: provavelmente outro corte ou outro episódio.",
    };

    /// <summary>Sincronizada, mas com diferença medida (de 0,2 s a ~0,6 s): o texto mostra quanto.</summary>
    private bool HasSmallOffset => Scale == 1 && Math.Abs(Offset.TotalMilliseconds) >= 200;

    /// <summary>"atrasada 2,3 s" (aparece depois da fala) ou "adiantada 2,3 s".</summary>
    private string OffsetText => (Offset > TimeSpan.Zero ? "adiantada " : "atrasada ") + Seconds;

    private string Seconds => Math.Abs(Offset.TotalSeconds).ToString("0.0", CultureInfo.CurrentCulture) + " s";
}

/// <summary>
/// Compara os tempos das falas de uma legenda com os de uma referência (outra legenda do mesmo vídeo, mesmo em
/// outro idioma). Em duas etapas:
/// <list type="number">
/// <item>Alinhamento: divide o tempo em blocos de 100 ms e procura o deslocamento (até ±60 s) e a taxa de quadros
/// em que mais fala da candidata cai sobre fala da referência.</item>
/// <item>Precisão: nota das falas da candidata pela distância até o início de uma fala da referência, sem ajuste
/// (inteira até 250 ms, zero a partir de 1 s), comparada com a do melhor alinhamento. Medir só a sobreposição seria
/// frouxo demais: com diálogo
/// denso, uma legenda 2 s atrasada ainda se sobrepõe em quase 90%.</item>
/// </list>
/// </summary>
public static class SubtitleSync
{
    public const int MinCues = 10;

    private const int BinMilliseconds = 100;
    private const int MaxOffsetBins = 60_000 / BinMilliseconds;

    /// <summary>Distância máxima entre os inícios para duas falas contarem como a mesma.</summary>
    private const double StartToleranceMilliseconds = 250;

    /// <summary>Refinamento do deslocamento em torno do melhor bloco de 100 ms.</summary>
    private const int RefineRangeMilliseconds = 500;
    private const int RefineStepMilliseconds = 25;

    /// <summary>A partir daqui (em relação ao melhor alinhamento) a legenda conta como sincronizada.</summary>
    internal const double SyncedThreshold = 0.60;

    /// <summary>Diferença a partir da qual uma fala já não ganha nota nenhuma.</summary>
    private const double MaxGradedMilliseconds = 1000;

    /// <summary>Abaixo disso, nem no melhor alinhamento as falas começam juntas: não é a mesma legenda.</summary>
    internal const double MinConfidence = 0.40;

    /// <summary>Fatores de tempo entre as taxas comuns (23,976, 24 e 25 fps).</summary>
    private static readonly double[] Scales =
    [
        1,
        25 / 23.976, 23.976 / 25,
        24 / 23.976, 23.976 / 24,
        25 / 24.0, 24 / 25.0,
    ];

    /// <summary><c>null</c> se alguma das legendas tiver falas de menos para comparar.</summary>
    public static SyncResult? Compare(IReadOnlyList<SubtitleCue> candidate, IReadOnlyList<SubtitleCue> reference)
    {
        if (candidate.Count < MinCues || reference.Count < MinCues) return null;

        var (scale, offsetBins) = BestAlignment(candidate, reference);
        var referenceStarts = reference.Select(c => c.Start.TotalMilliseconds).Order().ToArray();

        // O alinhamento por blocos acha a região certa; o refinamento acerta o deslocamento fino pelos inícios.
        var zero = StartAgreement(candidate, referenceStarts, 1, 0, null);
        double best = zero, bestOffset = 0, bestScale = 1;
        for (var delta = -RefineRangeMilliseconds; delta <= RefineRangeMilliseconds; delta += RefineStepMilliseconds)
        {
            var offset = offsetBins * BinMilliseconds + delta;
            var agreement = StartAgreement(candidate, referenceStarts, scale, offset, null);
            if (agreement > best + 1e-9) (best, bestOffset, bestScale) = (agreement, offset, scale);
        }

        // Vários deslocamentos empatam (a tolerância é de ±250 ms): a mediana das diferenças dá o valor exato.
        var differences = new List<double>();
        StartAgreement(candidate, referenceStarts, bestScale, bestOffset, differences);
        if (differences.Count > 0) bestOffset += differences.Order().ElementAt(differences.Count / 2);

        var offsetTime = TimeSpan.FromMilliseconds(Math.Round(bestOffset / 10) * 10);
        if (best < MinConfidence) return new SyncResult(zero, offsetTime, bestScale, SyncVerdict.Mismatch, best);

        // Precisão com nota gradual: até 250 ms vale inteiro, e cai até zero em 1 s. Assim 0,4 s de diferença
        // (que quase ninguém percebe, e muitas vezes é da própria referência) não derruba a nota para 35%.
        var precision = Math.Min(1, GradedAgreement(candidate, referenceStarts, 1, 0) / GradedAgreement(candidate, referenceStarts, bestScale, bestOffset));
        var verdict = precision >= SyncedThreshold ? SyncVerdict.Synced
            : bestScale != 1 ? SyncVerdict.FrameRate
            : SyncVerdict.Shifted;
        return new SyncResult(precision, offsetTime, bestScale, verdict, best);
    }

    /// <summary>Taxa de quadros e deslocamento (em blocos) em que mais fala da candidata cai sobre fala da referência.</summary>
    private static (double Scale, int OffsetBins) BestAlignment(IReadOnlyList<SubtitleCue> candidate, IReadOnlyList<SubtitleCue> reference)
    {
        var length = Bin(reference.Max(c => c.End)) + 1;
        var speech = new bool[length];
        foreach (var cue in reference)
        {
            for (var b = Bin(cue.Start); b < Math.Min(length, BinCeiling(cue.End)); b++) speech[b] = true;
        }

        var prefix = new int[length + 1];
        for (var i = 0; i < length; i++) prefix[i + 1] = prefix[i] + (speech[i] ? 1 : 0);

        double best = -1, bestScale = 1;
        var bestOffset = 0;
        foreach (var scale in Scales)
        {
            var starts = candidate.Select(c => (int)Math.Round(c.Start.TotalMilliseconds * scale / BinMilliseconds)).ToArray();
            var ends = candidate.Select(c => (int)Math.Round(c.End.TotalMilliseconds * scale / BinMilliseconds)).ToArray();
            for (var offset = -MaxOffsetBins; offset <= MaxOffsetBins; offset++)
            {
                long covered = 0, total = 0;
                for (var i = 0; i < starts.Length; i++)
                {
                    total += ends[i] - starts[i];
                    covered += prefix[Math.Clamp(ends[i] + offset, 0, length)] - prefix[Math.Clamp(starts[i] + offset, 0, length)];
                }

                // Em empate, fica o alinhamento mais simples (sem esticar, menor deslocamento).
                var coverage = total == 0 ? 0 : covered / (double)total;
                if (coverage > best + 1e-9 || (Math.Abs(coverage - best) <= 1e-9 && scale == bestScale && Math.Abs(offset) < Math.Abs(bestOffset)))
                    (best, bestScale, bestOffset) = (coverage, scale, offset);
            }
        }
        return (bestScale, bestOffset);
    }

    /// <summary>
    /// Fração das falas da candidata (já ajustadas) que começam perto do início de alguma fala da referência.
    /// Com <paramref name="differences"/>, guarda quanto falta somar a cada uma dessas falas para coincidir.
    /// </summary>
    private static double StartAgreement(IReadOnlyList<SubtitleCue> candidate, double[] referenceStarts, double scale, double offsetMilliseconds,
        List<double>? differences)
    {
        var hits = 0;
        foreach (var cue in candidate)
        {
            var start = cue.Start.TotalMilliseconds * scale + offsetMilliseconds;
            var index = Array.BinarySearch(referenceStarts, start);
            var difference = 0.0;
            if (index < 0)
            {
                index = ~index;
                var after = index < referenceStarts.Length ? referenceStarts[index] - start : double.MaxValue;
                var before = index > 0 ? referenceStarts[index - 1] - start : double.MinValue;
                difference = after <= -before ? after : before;
            }

            if (Math.Abs(difference) > StartToleranceMilliseconds) continue;
            hits++;
            differences?.Add(difference);
        }
        return hits / (double)candidate.Count;
    }

    /// <summary>
    /// Nota média das falas da candidata (já ajustadas): 1 se começam a até 250 ms de uma fala da referência,
    /// caindo em linha reta até 0 com 1 s de diferença.
    /// </summary>
    private static double GradedAgreement(IReadOnlyList<SubtitleCue> candidate, double[] referenceStarts, double scale, double offsetMilliseconds)
    {
        var total = 0.0;
        foreach (var cue in candidate)
        {
            var start = cue.Start.TotalMilliseconds * scale + offsetMilliseconds;
            var index = Array.BinarySearch(referenceStarts, start);
            if (index < 0) index = ~index;
            var nearest = Math.Min(
                index < referenceStarts.Length ? Math.Abs(referenceStarts[index] - start) : double.MaxValue,
                index > 0 ? Math.Abs(start - referenceStarts[index - 1]) : double.MaxValue);
            total += nearest <= StartToleranceMilliseconds ? 1
                : nearest >= MaxGradedMilliseconds ? 0
                : (MaxGradedMilliseconds - nearest) / (MaxGradedMilliseconds - StartToleranceMilliseconds);
        }
        return total / candidate.Count;
    }

    private static int Bin(TimeSpan time) => (int)(time.TotalMilliseconds / BinMilliseconds);

    private static int BinCeiling(TimeSpan time) => (int)Math.Ceiling(time.TotalMilliseconds / BinMilliseconds);
}
