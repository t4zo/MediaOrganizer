using System.Text;

namespace MediaOrganizer.Core.Tests;

/// <summary>Gera arquivos MKV mínimos (cabeçalho EBML + Segment com Tracks) para testar o leitor, sem binários no repositório.</summary>
public sealed class MkvBuilder
{
    private readonly List<byte[]> _tracks = [];

    /// <summary>Coloca um Cluster antes de Tracks; o leitor só acha as faixas pelo SeekHead.</summary>
    public bool TracksAtEnd { get; init; }

    public bool WithSeekHead { get; init; } = true;

    public MkvBuilder Video() => Add(1, null, null, null, false, false);

    public MkvBuilder Subtitle(string? language, string? bcp47 = null, string? name = null, bool forced = false, bool hearingImpaired = false) =>
        Add(17, language, bcp47, name, forced, hearingImpaired);

    private MkvBuilder Add(int type, string? language, string? bcp47, string? name, bool forced, bool hearingImpaired)
    {
        var children = new List<byte[]>
        {
            Element(0xD7, Uint((ulong)(_tracks.Count + 1))),
            Element(0x83, Uint((ulong)type)),
            Element(0x86, Encoding.ASCII.GetBytes(type == 17 ? "S_TEXT/UTF8" : "V_MPEG4/ISO/AVC")),
        };
        if (language is not null) children.Add(Element(0x22B59C, Encoding.ASCII.GetBytes(language)));
        if (bcp47 is not null) children.Add(Element(0x22B59D, Encoding.ASCII.GetBytes(bcp47)));
        if (name is not null) children.Add(Element(0x536E, Encoding.UTF8.GetBytes(name)));
        if (forced) children.Add(Element(0x55AA, Uint(1)));
        if (hearingImpaired) children.Add(Element(0x55AB, Uint(1)));
        _tracks.Add(Element(0xAE, [.. children.SelectMany(c => c)]));
        return this;
    }

    public byte[] Build()
    {
        var header = Element(0x1A45DFA3, Element(0x4282, Encoding.ASCII.GetBytes("matroska")));
        var tracks = Element(0x1654AE6B, [.. _tracks.SelectMany(t => t)]);
        var info = Element(0x1549A966, Element(0x2AD7B1, Uint(1_000_000)));
        var cluster = Element(0x1F43B675, Element(0xE7, Uint(0)));

        // SeekHead com posição de 8 bytes: tamanho fixo, então o deslocamento de Tracks pode ser calculado antes.
        var seekHeadSize = WithSeekHead ? SeekHead(0).Length : 0;
        var body = TracksAtEnd ? new[] { info, cluster } : [info];
        var tracksOffset = seekHeadSize + body.Sum(b => b.Length);

        var segment = new List<byte>();
        if (WithSeekHead) segment.AddRange(SeekHead((ulong)tracksOffset));
        foreach (var part in body) segment.AddRange(part);
        segment.AddRange(tracks);
        if (!TracksAtEnd) segment.AddRange(cluster);

        return [.. header, .. Element(0x18538067, [.. segment])];
    }

    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build());
        return path;
    }

    private static byte[] SeekHead(ulong tracksPosition) =>
        Element(0x114D9B74, Element(0x4DBB, [.. Element(0x53AB, Id(0x1654AE6B)), .. Element(0x53AC, Fixed8(tracksPosition))]));

    private static byte[] Element(uint id, byte[] data) => [.. Id(id), .. Size((ulong)data.Length), .. data];

    private static byte[] Id(uint id)
    {
        var bytes = BitConverter.GetBytes(id).Reverse().SkipWhile(b => b == 0).ToArray();
        return bytes.Length == 0 ? [0] : bytes;
    }

    /// <summary>Tamanho sempre em 8 bytes (marcador 0x01): válido e simples.</summary>
    private static byte[] Size(ulong size) => [0x01, .. Fixed8(size)[1..]];

    private static byte[] Fixed8(ulong value) => [.. BitConverter.GetBytes(value).Reverse()];

    private static byte[] Uint(ulong value)
    {
        var bytes = BitConverter.GetBytes(value).Reverse().SkipWhile(b => b == 0).ToArray();
        return bytes.Length == 0 ? [0] : bytes;
    }
}
