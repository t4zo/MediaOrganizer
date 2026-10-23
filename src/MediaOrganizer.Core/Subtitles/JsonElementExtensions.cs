using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Leitura tolerante das respostas dos provedores (campos ausentes, nulos ou com tipo trocado viram padrão).
/// As APIs de legenda mudam com frequência; um campo inesperado não deve derrubar a busca.
/// </summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? Str(this JsonElement element, string name) => element.Prop(name) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };

    public static long? Long(this JsonElement element, string name) => element.Prop(name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var n) => n,
        { ValueKind: JsonValueKind.String } v when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static int? Int(this JsonElement element, string name) => element.Long(name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    public static bool Bool(this JsonElement element, string name) => element.Prop(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.Number } v => v.TryGetInt32(out var n) && n != 0,
        { ValueKind: JsonValueKind.String } v => v.GetString() is "1" or "true" or "True",
        _ => false,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>Corpo JSON simples de um nível (login, download).</summary>
    public static string Object(params (string Name, object? Value)[] fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null: break;
                    case string s: writer.WriteString(name, s); break;
                    case long l: writer.WriteNumber(name, l); break;
                    case int i: writer.WriteNumber(name, i); break;
                    case bool b: writer.WriteBoolean(name, b); break;
                    default: writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
