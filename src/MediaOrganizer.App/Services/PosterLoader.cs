using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace MediaOrganizer.App.Services;

/// <summary>Baixa e mantém em memória os pôsteres do TMDb exibidos na interface.</summary>
public sealed class PosterLoader(HttpClient http)
{
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    public Task<Bitmap?> LoadAsync(string? url) =>
        url is null ? Task.FromResult<Bitmap?>(null) : _cache.GetOrAdd(url, DownloadAsync);

    private async Task<Bitmap?> DownloadAsync(string url)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(new Uri(url)).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // pôster é decorativo: falha silenciosa
        }
    }
}
