using System.IO.Enumeration;

namespace MediaOrganizer.Core.Scanning;

/// <summary>Reconhece arquivos de lixo de release pelos padrões configurados (curingas * e ?).</summary>
public sealed class JunkDetector(IEnumerable<string> patterns)
{
    private readonly string[] _patterns = [.. patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];

    public bool IsJunk(string fileName) =>
        _patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true));
}
