using System.Net;
using System.Text;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Tests;

/// <summary>Biblioteca temporária em disco com arquivos de mentira.</summary>
public sealed class TempLibrary : IDisposable
{
    public TempLibrary()
    {
        Base = Path.Combine(Path.GetTempPath(), "mediaorganizer-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Base, "Series");
        Directory.CreateDirectory(Root);
        Data = new DataPaths(Path.Combine(Base, "data"));
    }

    public string Base { get; }
    public string Root { get; }
    public DataPaths Data { get; }

    public string File(string relativePath, int size = 1)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public string Dir(string relativePath)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    public IReadOnlyList<string> Files() =>
        [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>TMDb falso: responde por "caminho?language=xx" (a query é casada por prefixo).</summary>
public sealed class FakeTmdb : HttpMessageHandler
{
    private readonly List<(string Prefix, string? Language, string Json)> _routes = [];

    public List<string> Requests { get; } = [];

    public FakeTmdb Route(string pathAndQuery, string json, string? language = null)
    {
        _routes.Add((pathAndQuery, language, json));
        return this;
    }

    public FakeTmdb Show(int id, string name, string originalName, string language, string firstAirDate = "2017-12-01") =>
        Route($"tv/{id}?language=en-US",
            $$"""{"id":{{id}},"name":"{{name}}","original_name":"{{originalName}}","original_language":"{{language}}","first_air_date":"{{firstAirDate}}","number_of_seasons":3}""");

    public FakeTmdb Search(string query, string language, params (int Id, string Name, string OriginalName, string Language)[] results) =>
        Route($"search/tv?query={Uri.EscapeDataString(query)}&",
            "{\"results\":[" + string.Join(',', results.Select(r =>
                $$"""{"id":{{r.Id}},"name":"{{r.Name}}","original_name":"{{r.OriginalName}}","original_language":"{{r.Language}}","first_air_date":"2017-12-01","popularity":50}""")) + "]}",
            language);

    public FakeTmdb Season(int id, int season, string language, params (int Episode, string Name)[] episodes) =>
        Route($"tv/{id}/season/{season}?language={language}",
            $$"""{"season_number":{{season}},"episodes":[{{string.Join(',', episodes.Select(e => $$"""{"episode_number":{{e.Episode}},"name":"{{e.Name}}"}"""))}}]}""");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri!.PathAndQuery.TrimStart('/');
        if (pathAndQuery.StartsWith("3/", StringComparison.Ordinal)) pathAndQuery = pathAndQuery[2..];
        Requests.Add(pathAndQuery);

        foreach (var (route, language, json) in _routes)
        {
            if (!pathAndQuery.StartsWith(route, StringComparison.OrdinalIgnoreCase)) continue;
            if (language is not null && !pathAndQuery.Contains($"language={language}", StringComparison.OrdinalIgnoreCase)) continue;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        var empty = pathAndQuery.StartsWith("search/", StringComparison.Ordinal) ? """{"results":[]}""" : null;
        return Task.FromResult(empty is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(empty, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Monta o grafo de serviços do Core sobre uma biblioteca temporária.</summary>
public sealed class Harness
{
    public Harness(TempLibrary library, FakeTmdb tmdb)
    {
        Settings = new SettingsStore(library.Data);
        Settings.Update(s => s.TmdbApiKey = "test-key");
        Processed = new ProcessedStore(library.Data);
        History = new HistoryStore(library.Data);
        var client = new TmdbClient(new HttpClient(tmdb), () => Settings.Current.TmdbApiKey);
        Analyzer = new LibraryAnalyzer(new MediaMatcher(client), new RenamePlanner(client), Settings, Processed);
        Executor = new RenameExecutor(History);
        Library = library;
    }

    public TempLibrary Library { get; }
    public SettingsStore Settings { get; }
    public ProcessedStore Processed { get; }
    public HistoryStore History { get; }
    public LibraryAnalyzer Analyzer { get; }
    public RenameExecutor Executor { get; }

    public async Task<List<FolderPlan>> AnalyzeAsync(MediaKind kind = MediaKind.Series, bool force = false)
    {
        var folders = Analyzer.Scan(Library.Root, kind);
        var plans = new List<FolderPlan>();
        await foreach (var plan in Analyzer.AnalyzeAsync(folders, new AnalyzeOptions(RenameSubtitles: true, Force: force), TestContext.Current.CancellationToken))
            plans.Add(plan);
        return [.. plans.OrderBy(p => p.Folder.Name, StringComparer.Ordinal)];
    }

    public ExecutionResult Apply(FolderPlan plan)
    {
        var items = plan.Items.Where(i => i.IsChange).ToList();
        var result = Executor.Apply(Library.Root, plan.Folder.Kind, "test", items, [plan.Folder.Key]);
        Analyzer.RecordApplied(plan, items);
        return result;
    }
}
