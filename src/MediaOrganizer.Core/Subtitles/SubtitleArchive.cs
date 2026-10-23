using System.IO.Compression;
using MediaOrganizer.Core.Parsing;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Extrai a legenda certa de um ZIP baixado (SubDL, SubSource). Tudo em memória, sem gravar caminhos do ZIP
/// em disco; ignora entradas com "..", lixo do macOS e arquivos que não são legenda, e limita o tamanho.
/// </summary>
public static class SubtitleArchive
{
    private const long MaxEntrySize = 20 * 1024 * 1024;
    private const long MaxTotalSize = 20 * 1024 * 1024;

    private static readonly string[] TextExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    public static bool IsZip(byte[] content) => content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4;

    /// <summary>
    /// Escolhe a legenda do episódio pedido (pelo SxxExx do nome) num pacote de temporada; num ZIP com uma
    /// só legenda, devolve essa. Prefere .srt.
    /// </summary>
    public static SubtitleFile Extract(byte[] zip, int? season, int? episode, string provider)
    {
        List<(string Name, string Extension, byte[] Content)> entries;
        try
        {
            entries = ReadEntries(zip);
        }
        catch (InvalidDataException e)
        {
            throw new SubtitleProviderException(provider, SubtitleProviderError.InvalidContent, $"Arquivo ZIP inválido: {e.Message}");
        }

        if (entries.Count == 0)
            throw new SubtitleProviderException(provider, SubtitleProviderError.InvalidContent, "O pacote baixado não contém legendas.");

        var chosen = Preferred(entries)!.Value;
        if (episode is { } wanted && entries.Count > 1)
        {
            var matching = entries.Where(e => ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(e.Name)) is { } parsed &&
                                              (season is null || parsed.Season == season) &&
                                              wanted >= parsed.Episode && wanted <= (parsed.LastEpisode ?? parsed.Episode)).ToList();
            chosen = Preferred(matching)
                     ?? throw new SubtitleProviderException(provider, SubtitleProviderError.InvalidContent,
                         $"O episódio S{season:00}E{wanted:00} não está no pacote baixado.");
        }

        return new SubtitleFile(chosen.Content, chosen.Extension.TrimStart('.'));
    }

    private static (string Name, string Extension, byte[] Content)? Preferred(List<(string Name, string Extension, byte[] Content)> entries) =>
        entries.Count == 0 ? null : entries.OrderBy(e => Array.IndexOf(TextExtensions, e.Extension)).First();

    private static List<(string Name, string Extension, byte[] Content)> ReadEntries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var result = new List<(string, string, byte[])>();
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var file = name[(name.LastIndexOf('/') + 1)..];
            if (file.Length == 0 || name.Contains("../", StringComparison.Ordinal) || name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                file.StartsWith("._", StringComparison.Ordinal))
                continue;

            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (!TextExtensions.Contains(extension)) continue;
            if (entry.Length > MaxEntrySize || (total += entry.Length) > MaxTotalSize)
                throw new InvalidDataException("conteúdo grande demais para uma legenda.");

            // Lê com limite: o tamanho declarado no cabeçalho do ZIP pode mentir (zip bomb).
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxEntrySize) throw new InvalidDataException("conteúdo grande demais para uma legenda.");
                buffer.Write(chunk, 0, read);
            }
            result.Add((file, extension, buffer.ToArray()));
        }

        return result;
    }
}
