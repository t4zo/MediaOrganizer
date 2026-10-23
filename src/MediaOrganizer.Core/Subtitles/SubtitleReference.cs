using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma legenda que o vídeo já tem, usada como referência de tempo, e os tempos das falas dela.</summary>
public sealed record SyncReference(ExistingSubtitle Source, IReadOnlyList<SubtitleCue> Cues)
{
    public string Description => SubtitleReferenceProvider.Describe(Source);
}

/// <summary>Qual legenda serve de referência para um vídeo, ou por que nenhuma serve.</summary>
public sealed record SyncReferenceChoice(ExistingSubtitle? Source, string? Reason)
{
    public bool IsAvailable => Source is not null;
}

/// <summary>
/// Acha e lê a legenda de referência de um vídeo: um arquivo de texto ao lado do vídeo (em qualquer idioma) ou
/// uma faixa de texto do MKV, extraída com o mkvextract. Faixas de imagem (PGS, VobSub) não servem.
/// Os tempos extraídos ficam em cache, porque o mkvextract lê o vídeo inteiro.
/// </summary>
public sealed class SubtitleReferenceProvider(IMkvMergeRunner runner, Func<string?> configuredMkvMerge, string cacheDirectory)
{
    private static readonly FrozenSet<string> TextCodecs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "S_TEXT/UTF8", "S_TEXT/ASCII", "S_TEXT/ASS", "S_TEXT/SSA", "S_TEXT/WEBVTT", "S_ASS", "S_SSA");

    public string? ExtractToolPath => MkvMergeLocator.FindExtract(configuredMkvMerge());

    public static bool IsTextCodec(string? codec) => codec is not null && TextCodecs.Contains(codec);

    /// <summary>"en (embutida)", "pt-BR (arquivo)".</summary>
    public static string Describe(ExistingSubtitle source) => source.Language + (source.Embedded ? " (embutida)" : " (arquivo)");

    /// <summary>
    /// Arquivo antes de faixa embutida (não precisa ler o vídeo); legendas completas antes das forçadas, que só
    /// têm poucas falas; sem SDH antes de SDH, cujas descrições de som não são fala.
    /// </summary>
    public SyncReferenceChoice Choose(VideoSubtitleState state)
    {
        var usable = state.Existing.Where(e => !e.Language.Forced).ToList();
        var files = usable.Where(e => !e.Embedded && e.Path is not null && SubtitleText.IsText(Path.GetExtension(e.Path)) && File.Exists(e.Path));
        var tracks = usable.Where(e => e.Embedded && e.TrackIndex is not null && IsTextCodec(e.CodecId)).ToList();

        if (files.OrderBy(e => e.Language.HearingImpaired).FirstOrDefault() is { } file) return new SyncReferenceChoice(file, null);

        if (tracks.Count > 0)
        {
            return ExtractToolPath is null
                ? new SyncReferenceChoice(null, "Para usar a legenda embutida como referência, instale o MKVToolNix (mkvextract).")
                : new SyncReferenceChoice(tracks.OrderBy(e => e.Language.HearingImpaired).First(), null);
        }

        return new SyncReferenceChoice(null, state.Existing.Any(e => e.Embedded && !e.Language.Forced)
            ? "As legendas embutidas no vídeo são imagens (PGS/VobSub): não dá para comparar os tempos."
            : "O vídeo não tem nenhuma legenda para servir de referência.");
    }

    /// <summary>Lê os tempos da referência escolhida. Falha de leitura ou do mkvextract vira <see cref="IOException"/>.</summary>
    public async Task<SyncReference> LoadAsync(string videoPath, ExistingSubtitle source, IProgress<double>? progress, CancellationToken ct)
    {
        if (!source.Embedded)
        {
            var bytes = await File.ReadAllBytesAsync(source.Path!, ct).ConfigureAwait(false);
            return new SyncReference(source, SubtitleCues.Parse(bytes));
        }

        var cacheFile = CacheFile(videoPath, source.TrackIndex!.Value);
        if (ReadCached(cacheFile) is { } cached) return new SyncReference(source, cached);

        var cues = await ExtractAsync(videoPath, source, progress, ct).ConfigureAwait(false);
        WriteCached(cacheFile, cues);
        return new SyncReference(source, cues);
    }

    private async Task<IReadOnlyList<SubtitleCue>> ExtractAsync(string videoPath, ExistingSubtitle source, IProgress<double>? progress, CancellationToken ct)
    {
        var tool = ExtractToolPath ?? throw new IOException("mkvextract não encontrado. Instale o MKVToolNix ou informe o caminho do mkvmerge nas Configurações.");
        var extension = source.CodecId!.Contains("ASS", StringComparison.OrdinalIgnoreCase) || source.CodecId.Contains("SSA", StringComparison.OrdinalIgnoreCase)
            ? ".ass"
            : source.CodecId.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase) ? ".vtt" : ".srt";
        var temp = Path.Combine(cacheDirectory, "tmp", Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

        try
        {
            var spec = source.TrackIndex!.Value.ToString(CultureInfo.InvariantCulture) + ":" + temp;
            var result = await runner.RunAsync(tool, ["--gui-mode", videoPath, "tracks", spec], progress, ct).ConfigureAwait(false);
            if (!result.Succeeded || !File.Exists(temp)) throw new IOException($"O mkvextract falhou: {result.ErrorText}");
            return SubtitleCues.Parse(await File.ReadAllBytesAsync(temp, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException($"Não foi possível executar o mkvextract: {e.Message}", e);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A chave inclui tamanho e data do vídeo: se ele mudar (remux, outra versão), extrai de novo.</summary>
    private string CacheFile(string videoPath, int trackIndex)
    {
        var info = new FileInfo(videoPath);
        var key = $"{Path.GetFullPath(videoPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{trackIndex}";
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(key));
        return Path.Combine(cacheDirectory, Convert.ToHexStringLower(hash) + ".txt");
    }

    /// <summary>Uma fala por linha: "início fim" em milissegundos.</summary>
    private static List<SubtitleCue>? ReadCached(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var cues = new List<SubtitleCue>();
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split(' ');
                if (parts.Length != 2 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                    !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
                    return null;
                cues.Add(new SubtitleCue(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end)));
            }
            return cues;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCached(string file, IReadOnlyList<SubtitleCue> cues)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, cues.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{(long)c.Start.TotalMilliseconds} {(long)c.End.TotalMilliseconds}")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
