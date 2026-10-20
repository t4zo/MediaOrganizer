using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MediaOrganizer.Core.State;

/// <summary>Leitura e gravação atômica de arquivos JSON (grava em .tmp e substitui).</summary>
public static class JsonStore
{
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? fallback();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
        }
        File.Move(temp, path, overwrite: true);
    }
}
