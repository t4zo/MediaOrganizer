namespace MediaOrganizer.Core.State;

/// <summary>
/// Localiza a pasta <c>data/</c> do app (portátil, igual em Windows, macOS e Linux).
/// Ordem: variável <c>MEDIAORGANIZER_DATA</c> → pasta do projeto (desenvolvimento) →
/// ao lado do executável (ou do <c>.app</c> no macOS) → pasta de dados do usuário, se a anterior não for gravável.
/// </summary>
public sealed class DataPaths
{
    public const string EnvironmentVariable = "MEDIAORGANIZER_DATA";
    private const string SolutionFile = "MediaOrganizer.slnx";

    public DataPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProcessedFile => Path.Combine(Root, "processed.json");
    public string HistoryDirectory => Path.Combine(Root, "history");
    public string CacheDirectory => Path.Combine(Root, "cache");

    public static DataPaths Resolve()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom)
            return new DataPaths(Path.GetFullPath(custom));

        var baseDir = AppContext.BaseDirectory;
        var candidate = FindProjectRoot(baseDir) ?? AppBundleParent(baseDir) ?? baseDir;
        var dataDir = Path.Combine(candidate, "data");

        if (IsWritable(dataDir)) return new DataPaths(dataDir);

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "MediaOrganizer");
        return new DataPaths(fallback);
    }

    /// <summary>Em desenvolvimento (<c>dotnet run</c>) os dados ficam na raiz do repositório.</summary>
    private static string? FindProjectRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
        }
        return null;
    }

    /// <summary>No macOS o executável fica em <c>X.app/Contents/MacOS</c>; os dados vão ao lado do <c>.app</c>.</summary>
    private static string? AppBundleParent(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } bundle } } &&
            bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return bundle.Parent?.FullName;
        }
        return null;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
