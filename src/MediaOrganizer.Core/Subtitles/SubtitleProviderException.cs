namespace MediaOrganizer.Core.Subtitles;

public enum SubtitleProviderError
{
    /// <summary>Cota diária de downloads esgotada: o provedor fica desativado até renovar.</summary>
    QuotaExceeded,
    /// <summary>Limite de requisições por minuto.</summary>
    RateLimited,
    /// <summary>Chave ou login recusados.</summary>
    AuthenticationFailed,
    /// <summary>Servidor fora do ar, tempo esgotado ou resposta inesperada.</summary>
    Unavailable,
    /// <summary>O arquivo baixado não contém a legenda esperada.</summary>
    InvalidContent,
}

public sealed class SubtitleProviderException : Exception
{
    public SubtitleProviderException(string provider, SubtitleProviderError error, string message, DateTimeOffset? resetAt = null)
        : base(message)
    {
        Provider = provider;
        Error = error;
        ResetAt = resetAt;
    }

    public SubtitleProviderException()
    {
        Provider = "";
    }

    public SubtitleProviderException(string message) : base(message)
    {
        Provider = "";
    }

    public SubtitleProviderException(string message, Exception innerException) : base(message, innerException)
    {
        Provider = "";
    }

    public string Provider { get; }

    public SubtitleProviderError Error { get; }

    /// <summary>Quando a cota renova, se o provedor informou.</summary>
    public DateTimeOffset? ResetAt { get; }
}
