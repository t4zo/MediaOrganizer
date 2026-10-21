using System.Buffers.Binary;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Hash de arquivo do OpenSubtitles: tamanho + soma (com estouro) dos blocos de 64 KB iniciais e finais lidos
/// como <c>ulong</c> little-endian, em 16 dígitos hexadecimais. Lê só 128 KB, rápido mesmo em HD externo.
/// </summary>
public static class OpenSubtitlesHash
{
    private const int ChunkSize = 64 * 1024;

    /// <returns>O hash, ou <c>null</c> se o arquivo não puder ser lido.</returns>
    public static string? TryCompute(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            return Compute(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Compute(Stream stream)
    {
        var length = stream.Length;
        var hash = (ulong)length;
        var buffer = new byte[ChunkSize];

        hash = unchecked(hash + SumChunk(stream, 0, buffer));
        hash = unchecked(hash + SumChunk(stream, Math.Max(0, length - ChunkSize), buffer));
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong SumChunk(Stream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        ulong sum = 0;
        for (var i = 0; i + 8 <= read; i += 8) sum = unchecked(sum + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
        return sum;
    }
}
