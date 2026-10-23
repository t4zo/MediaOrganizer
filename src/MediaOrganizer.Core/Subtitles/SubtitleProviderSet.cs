using System.Collections.Concurrent;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Os provedores na ordem de consulta e quais estão fora de uso nesta sessão (cota esgotada até renovar,
/// chave recusada até as configurações mudarem).
/// </summary>
public sealed class SubtitleProviderSet(IEnumerable<ISubtitleProvider> providers, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (string Reason, DateTimeOffset Until)> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ISubtitleProvider> All { get; } = [.. providers];

    /// <summary>Configurados e não desativados, na ordem de consulta.</summary>
    public IReadOnlyList<ISubtitleProvider> Active => [.. All.Where(p => p.IsConfigured && DisabledReason(p) is null)];

    public ISubtitleProvider? Find(string name) => All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string? DisabledReason(ISubtitleProvider provider)
    {
        if (!_disabled.TryGetValue(provider.Name, out var entry)) return null;
        if (time.GetUtcNow() < entry.Until) return entry.Reason;
        _disabled.TryRemove(provider.Name, out _);
        return null;
    }

    /// <summary>Tira o provedor de uso se o erro for de cota ou de autenticação; outros erros são passageiros.</summary>
    public void Report(SubtitleProviderException error)
    {
        var until = error.Error switch
        {
            SubtitleProviderError.QuotaExceeded => error.ResetAt ?? DateTimeOffset.MaxValue,
            SubtitleProviderError.AuthenticationFailed => DateTimeOffset.MaxValue,
            _ => (DateTimeOffset?)null,
        };
        if (until is { } u) _disabled[error.Provider] = (error.Message, u);
    }

    /// <summary>Depois de salvar as configurações (chave nova, provedor reativado).</summary>
    public void ResetDisabled() => _disabled.Clear();

    /// <summary>Avisos para o topo da tela: nenhum provedor configurado, cota esgotada, chave recusada.</summary>
    public IReadOnlyList<string> Notices()
    {
        if (!All.Any(p => p.IsConfigured))
            return ["Nenhum site de legendas configurado. Informe a chave do OpenSubtitles, SubDL ou SubSource nas Configurações."];
        return [.. All.Where(p => p.IsConfigured).Select(DisabledReason).OfType<string>()];
    }
}
