using System.Text;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Converte legendas de texto para UTF-8 (com BOM, que players antigos e TVs reconhecem melhor).
/// Legendas antigas costumam vir em Windows-1252; sem converter, os acentos quebram nos players e no remux.
/// </summary>
public static class SubtitleText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>Formatos de texto; os de imagem (.sup, .sub/.idx) passam sem conversão.</summary>
    public static bool IsText(string extension) =>
        extension.TrimStart('.').ToLowerInvariant() is "srt" or "ass" or "ssa" or "vtt";

    public static byte[] ToUtf8(byte[] content)
    {
        var text = Decode(content);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    public static string Decode(byte[] content)
    {
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252.Value.GetString(bytes);
        }
    }
}
