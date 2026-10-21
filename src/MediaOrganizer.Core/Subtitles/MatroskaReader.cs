using System.Text;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma faixa de um arquivo MKV. <see cref="Index"/> é a posição na lista (o "track ID" do mkvmerge).</summary>
public sealed record MatroskaTrack(
    int Index,
    int Number,
    int Type,
    string? CodecId,
    string Language,
    string? LanguageBcp47,
    string? Name,
    bool Default,
    bool Forced,
    bool HearingImpaired)
{
    public const int SubtitleType = 17;

    public bool IsSubtitle => Type == SubtitleType;
}

/// <summary>
/// Leitor EBML mínimo: só a lista de faixas (Segment → Tracks), sem ffprobe. Usa o SeekHead para pular direto
/// até Tracks e lê poucos KB. Arquivo corrompido ou que não é MKV devolve lista vazia; nunca lança.
/// </summary>
public static class MatroskaReader
{
    private const uint EbmlHeaderId = 0x1A45DFA3;
    private const uint SegmentId = 0x18538067;
    private const uint SeekHeadId = 0x114D9B74;
    private const uint SeekId = 0x4DBB;
    private const uint SeekIdId = 0x53AB;
    private const uint SeekPositionId = 0x53AC;
    private const uint TracksId = 0x1654AE6B;
    private const uint TrackEntryId = 0xAE;
    private const uint TrackNumberId = 0xD7;
    private const uint TrackTypeId = 0x83;
    private const uint CodecIdId = 0x86;
    private const uint NameId = 0x536E;
    private const uint LanguageId = 0x22B59C;
    private const uint LanguageBcp47Id = 0x22B59D;
    private const uint FlagDefaultId = 0x88;
    private const uint FlagForcedId = 0x55AA;
    private const uint FlagHearingImpairedId = 0x55AB;
    private const uint ClusterId = 0x1F43B675;

    /// <summary>Elementos de topo percorridos antes de desistir (sem SeekHead, Tracks costuma estar no começo).</summary>
    private const int MaxTopLevelElements = 64;

    /// <summary>Tamanho máximo aceito para o elemento Tracks (proteção contra arquivo corrompido).</summary>
    private const long MaxTracksSize = 4 * 1024 * 1024;

    private const ulong UnknownSize = ulong.MaxValue;

    public static IReadOnlyList<MatroskaTrack> ReadTracks(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return ReadTracks(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<MatroskaTrack> ReadTracks(Stream stream)
    {
        try
        {
            return ReadCore(stream);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    public static bool IsMatroska(string path) =>
        Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".mka", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase);

    private static List<MatroskaTrack> ReadCore(Stream stream)
    {
        var (headerId, headerSize) = ReadElementHeader(stream);
        if (headerId != EbmlHeaderId || headerSize == UnknownSize) return [];
        Skip(stream, headerSize);

        var (segmentId, segmentSize) = ReadElementHeader(stream);
        if (segmentId != SegmentId) return [];
        var segmentStart = stream.Position;
        var segmentEnd = segmentSize == UnknownSize ? stream.Length : Math.Min(stream.Length, segmentStart + (long)segmentSize);

        long? tracksPosition = null;
        for (var count = 0; count < MaxTopLevelElements && stream.Position < segmentEnd; count++)
        {
            var (id, size) = ReadElementHeader(stream);
            if (id == TracksId) return ReadTracksElement(stream, size);

            if (id == SeekHeadId && size != UnknownSize)
            {
                tracksPosition ??= ReadSeekHead(stream, (long)size, segmentStart);
                if (tracksPosition is { } position && position > stream.Position && position < segmentEnd) stream.Position = position;
                continue;
            }

            if (id == ClusterId || size == UnknownSize)
            {
                // Chegou aos dados sem achar Tracks pelo caminho: só um SeekHead pode ajudar.
                if (tracksPosition is not { } target || target >= segmentEnd) return [];
                stream.Position = target;
                var (trackId, trackSize) = ReadElementHeader(stream);
                return trackId == TracksId ? ReadTracksElement(stream, trackSize) : [];
            }

            Skip(stream, size);
        }

        return [];
    }

    private static long? ReadSeekHead(Stream stream, long size, long segmentStart)
    {
        var end = stream.Position + size;
        long? result = null;
        while (stream.Position < end)
        {
            var (id, seekSize) = ReadElementHeader(stream);
            if (id != SeekId)
            {
                Skip(stream, seekSize);
                continue;
            }

            var seekEnd = stream.Position + (long)seekSize;
            uint target = 0;
            long? position = null;
            while (stream.Position < seekEnd)
            {
                var (childId, childSize) = ReadElementHeader(stream);
                if (childId == SeekIdId) target = (uint)ReadUnsigned(stream, childSize);
                else if (childId == SeekPositionId) position = (long)ReadUnsigned(stream, childSize);
                else Skip(stream, childSize);
            }
            if (target == TracksId && position is { } p) result = segmentStart + p;
        }
        stream.Position = end;
        return result;
    }

    private static List<MatroskaTrack> ReadTracksElement(Stream stream, ulong size)
    {
        if (size == UnknownSize || size > MaxTracksSize) throw new InvalidDataException("Tracks inválido.");
        var end = stream.Position + (long)size;
        var tracks = new List<MatroskaTrack>();

        while (stream.Position < end)
        {
            var (id, entrySize) = ReadElementHeader(stream);
            if (id != TrackEntryId)
            {
                Skip(stream, entrySize);
                continue;
            }
            tracks.Add(ReadTrackEntry(stream, (long)entrySize, tracks.Count));
        }
        return tracks;
    }

    private static MatroskaTrack ReadTrackEntry(Stream stream, long size, int index)
    {
        var end = stream.Position + size;
        int number = 0, type = 0;
        string? codec = null, name = null, bcp47 = null;
        var language = "eng"; // padrão da especificação quando o elemento falta
        bool isDefault = true, forced = false, hearingImpaired = false;

        while (stream.Position < end)
        {
            var (id, childSize) = ReadElementHeader(stream);
            switch (id)
            {
                case TrackNumberId: number = (int)ReadUnsigned(stream, childSize); break;
                case TrackTypeId: type = (int)ReadUnsigned(stream, childSize); break;
                case CodecIdId: codec = ReadString(stream, childSize); break;
                case NameId: name = ReadString(stream, childSize); break;
                case LanguageId: language = ReadString(stream, childSize); break;
                case LanguageBcp47Id: bcp47 = ReadString(stream, childSize); break;
                case FlagDefaultId: isDefault = ReadUnsigned(stream, childSize) != 0; break;
                case FlagForcedId: forced = ReadUnsigned(stream, childSize) != 0; break;
                case FlagHearingImpairedId: hearingImpaired = ReadUnsigned(stream, childSize) != 0; break;
                default: Skip(stream, childSize); break;
            }
        }

        stream.Position = end;
        return new MatroskaTrack(index, number, type, codec, language, bcp47, name, isDefault, forced, hearingImpaired);
    }

    /// <summary>ID (com o marcador de tamanho, como na especificação) e tamanho do dado.</summary>
    private static (uint Id, ulong Size) ReadElementHeader(Stream stream)
    {
        var first = ReadByte(stream);
        var idLength = LeadingLength(first, 4);
        uint id = first;
        for (var i = 1; i < idLength; i++) id = (id << 8) | ReadByte(stream);

        var sizeFirst = ReadByte(stream);
        var sizeLength = LeadingLength(sizeFirst, 8);
        var mask = (byte)(0xFF >> sizeLength);
        ulong size = (ulong)(sizeFirst & mask);
        var allOnes = (sizeFirst & mask) == mask;
        for (var i = 1; i < sizeLength; i++)
        {
            var b = ReadByte(stream);
            allOnes &= b == 0xFF;
            size = (size << 8) | b;
        }

        return (id, allOnes ? UnknownSize : size);
    }

    private static int LeadingLength(byte first, int max)
    {
        for (var length = 1; length <= max; length++)
        {
            if ((first & (0x80 >> (length - 1))) != 0) return length;
        }
        throw new InvalidDataException("Inteiro EBML inválido.");
    }

    private static ulong ReadUnsigned(Stream stream, ulong size)
    {
        if (size > 8) throw new InvalidDataException("Inteiro grande demais.");
        ulong value = 0;
        for (var i = 0UL; i < size; i++) value = (value << 8) | ReadByte(stream);
        return value;
    }

    private static string ReadString(Stream stream, ulong size)
    {
        if (size > 4096) throw new InvalidDataException("Texto grande demais.");
        var buffer = new byte[(int)size];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new EndOfStreamException() : (byte)value;
    }

    private static void Skip(Stream stream, ulong size)
    {
        if (size > (ulong)(stream.Length - stream.Position)) throw new EndOfStreamException();
        stream.Position += (long)size;
    }
}
