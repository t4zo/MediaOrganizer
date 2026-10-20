using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;

namespace MediaOrganizer.Core.Metadata;

/// <summary>
/// Cache das respostas do TMDb em memória e em disco (<c>data/cache</c>),
/// para reanálises rápidas e para respeitar o limite de requisições da API.
/// </summary>
public sealed class MetadataCache(string directory, TimeSpan timeToLive)
{
    private readonly ConcurrentDictionary<string, string> _memory = new();

    public static TimeSpan DefaultTimeToLive { get; } = TimeSpan.FromDays(7);

    public string? Get(string key)
    {
        if (_memory.TryGetValue(key, out var cached)) return cached;

        var file = FileFor(key);
        if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > timeToLive) return null;

        try
        {
            var json = File.ReadAllText(file);
            _memory[key] = json;
            return json;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Set(string key, string json)
    {
        _memory[key] = json;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(FileFor(key), json);
        }
        catch (IOException)
        {
            // Falha de cache em disco não deve interromper a análise.
        }
    }

    public void Clear()
    {
        _memory.Clear();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string FileFor(string key)
    {
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(key));
        return Path.Combine(directory, Convert.ToHexStringLower(hash) + ".json");
    }
}
