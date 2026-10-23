using System.Net;
using System.Net.Sockets;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Envio HTTP dos provedores de legenda. Diferente do TMDb, não repete todo 429: nesses sites um 429/406
/// pode significar cota diária esgotada. Repete só falhas de rede/5xx (2 vezes) e 429 com Retry-After curto (1 vez).
/// </summary>
internal static class ProviderHttp
{
    public const string UserAgent = "MediaOrganizer v1.2.0";

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    /// <summary>Espera entre tentativas (os testes zeram).</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> build, string provider, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = build();
                request.Headers.UserAgent.ParseAdd(UserAgent);
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt < 2)
                {
                    await Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }
                throw new SubtitleProviderException(provider, SubtitleProviderError.Unavailable, $"{provider} não respondeu: {e.Message}");
            }

            if ((int)response.StatusCode >= 500 && attempt < 2)
            {
                response.Dispose();
                await Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0 && RetryAfter(response) is { } wait && wait <= MaxRetryAfter)
            {
                response.Dispose();
                await Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    /// <summary>Lança <see cref="SubtitleProviderError.Unavailable"/> para 5xx e respostas inesperadas.</summary>
    public static SubtitleProviderException Unexpected(string provider, HttpResponseMessage response) =>
        new(provider, SubtitleProviderError.Unavailable, $"{provider} respondeu HTTP {(int)response.StatusCode}.");

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retry) return null;
        if (retry.Delta is { } delta) return delta;
        return retry.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }
}
