namespace MediaOrganizer.Core.Scanning;

/// <summary>
/// Uma série (pasta) ou um filme (pasta ou arquivo solto) encontrado na biblioteca.
/// Todos os caminhos são absolutos.
/// </summary>
public sealed record LibraryFolder(
    MediaKind Kind,
    string RootPath,
    string Path,
    string Name,
    bool IsLooseFile,
    IReadOnlyList<string> Videos,
    IReadOnlyList<string> Subtitles,
    IReadOnlyList<string> Junk,
    IReadOnlyList<string> EmptyDirectories)
{
    /// <summary>Vídeos que estão dentro de uma pasta de especiais ("Specials", "Season 00").</summary>
    public IReadOnlySet<string> SpecialVideos { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Chave portátil entre sistemas: "Series/&lt;nome da raiz&gt;/&lt;pasta&gt;".</summary>
    public string Key => KeyFor(Kind, RootPath, Name);

    /// <summary>Chave da correspondência com o TMDb (independe da raiz).</summary>
    public string MatchKey => $"{Kind}/{Name}";

    public static string KeyFor(MediaKind kind, string rootPath, string name) =>
        $"{kind}/{System.IO.Path.GetFileName(rootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))}/{name}";
}
