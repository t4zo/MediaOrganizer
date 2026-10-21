namespace MediaOrganizer.Core.Scanning;

/// <summary>
/// Varre uma pasta raiz da biblioteca. Para séries, cada subpasta direta é uma série
/// (os arquivos permanecem nas subpastas onde estão). Para filmes, cada subpasta ou vídeo solto é um filme.
/// </summary>
/// <param name="includeSpecials">
/// Pastas de especiais ("Specials", "Season 00") são varridas quando <c>true</c> e ignoradas quando <c>false</c>,
/// independentemente da lista de pastas ignoradas.
/// </param>
public sealed partial class LibraryScanner(IEnumerable<string> ignoredFolders, JunkDetector junkDetector, bool includeSpecials = false)
{
    private readonly HashSet<string> _ignored = new(ignoredFolders, StringComparer.OrdinalIgnoreCase);

    public static bool IsSpecialsFolder(string name) => SpecialsFolderRegex().IsMatch(name);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public IReadOnlyList<LibraryFolder> Scan(string rootPath, MediaKind kind)
    {
        var root = Path.GetFullPath(rootPath);
        var result = new List<LibraryFolder>();

        foreach (var dir in Directory.EnumerateDirectories(root, "*", Options).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSkipped(Path.GetFileName(dir))) continue;
            var folder = ScanFolder(kind, root, dir);
            if (folder.Videos.Count > 0 || folder.Junk.Count > 0) result.Add(folder);
        }

        if (kind == MediaKind.Movie) result.AddRange(ScanLooseMovies(root));
        return result;
    }

    /// <summary>Reescaneia uma única pasta (após aplicar/desfazer, para recalcular o fingerprint).</summary>
    public LibraryFolder ScanFolder(MediaKind kind, string rootPath, string folderPath)
    {
        var videos = new List<string>();
        var subtitles = new List<string>();
        var junk = new List<string>();
        var emptyDirs = new List<string>();
        var specials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Collect(folderPath, videos, subtitles, junk, emptyDirs, specials, isTop: true, inSpecials: false);

        if (kind == MediaKind.Movie && videos.Count > 1)
        {
            // O filme é o maior vídeo; demais (trailers, extras soltos) são mantidos como estão.
            var main = videos.MaxBy(v => new FileInfo(v).Length)!;
            videos = [main];
        }

        return new LibraryFolder(kind, rootPath, folderPath, Path.GetFileName(folderPath), IsLooseFile: false,
            videos, subtitles, junk, emptyDirs)
        {
            SpecialVideos = specials.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static IEnumerable<LibraryFolder> ScanLooseMovies(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", Options).ToList();
        foreach (var video in files.Where(MediaFileTypes.IsVideo).Order(StringComparer.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(video);
            var subtitles = files
                .Where(f => MediaFileTypes.IsSubtitle(f) && Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            yield return new LibraryFolder(MediaKind.Movie, root, root, baseName, IsLooseFile: true, [video], subtitles, [], []);
        }
    }

    /// <returns><c>true</c> se a pasta contém algum arquivo (direta ou indiretamente).</returns>
    private bool Collect(string dir, List<string> videos, List<string> subtitles, List<string> junk, List<string> emptyDirs,
        HashSet<string> specials, bool isTop, bool inSpecials)
    {
        var hasFiles = false;

        foreach (var file in Directory.EnumerateFiles(dir, "*", Options))
        {
            hasFiles = true;
            var name = Path.GetFileName(file);
            if (junkDetector.IsJunk(name)) junk.Add(file);
            else if (MediaFileTypes.IsVideo(name))
            {
                videos.Add(file);
                if (inSpecials) specials.Add(file);
            }
            else if (MediaFileTypes.IsSubtitle(name)) subtitles.Add(file);
        }

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", Options))
        {
            var name = Path.GetFileName(sub);
            if (IsSkipped(name))
            {
                hasFiles = true; // pastas ignoradas nunca são consideradas vazias
                continue;
            }
            hasFiles |= Collect(sub, videos, subtitles, junk, emptyDirs, specials, isTop: false,
                inSpecials: inSpecials || IsSpecialsFolder(name));
        }

        if (!hasFiles && !isTop) emptyDirs.Add(dir);
        return hasFiles;
    }

    private bool IsSkipped(string name) =>
        name.StartsWith('.') || name.StartsWith('$') ||
        (IsSpecialsFolder(name) ? !includeSpecials : _ignored.Contains(name));

    // "Specials", "Special", "Season 00", "Season 0", "S00"
    [GeneratedRegex(@"^(specials?|season\s*0+|s0+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderRegex();
}
