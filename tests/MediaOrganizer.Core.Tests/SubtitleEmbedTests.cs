using System.Text;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

/// <summary>
/// mkvmerge falso: lê as faixas do MKV de entrada e grava a saída com elas + as legendas pedidas
/// (ou sem as faixas de "--subtitle-tracks !ids"), usando o <see cref="MkvBuilder"/>.
/// </summary>
public sealed class FakeMkvMergeRunner : IMkvMergeRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public int ExitCode { get; set; }
    public bool DropFirstTrack { get; set; }

    public Task<MkvMergeResult> RunAsync(string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken ct)
    {
        Calls.Add(arguments);
        if (arguments.Contains("--version")) return Task.FromResult(new MkvMergeResult(0, ["mkvmerge v80.0 ('Roundabout') 64-bit"]));
        if (ExitCode == 2) return Task.FromResult(new MkvMergeResult(2, ["#GUI#error Algo deu errado."]));

        var output = arguments[arguments.ToList().IndexOf("-o") + 1];
        var input = arguments[^1];
        var builder = new MkvBuilder();
        var removed = new HashSet<int>();
        var removeIndex = arguments.ToList().IndexOf("--subtitle-tracks");
        if (removeIndex >= 0)
        {
            input = arguments[removeIndex + 2];
            removed = [.. arguments[removeIndex + 1].TrimStart('!').Split(',').Select(int.Parse)];
        }
        else
        {
            input = arguments[arguments.ToList().IndexOf("-o") + 2];
        }

        foreach (var track in MatroskaReader.ReadTracks(input).Skip(DropFirstTrack ? 1 : 0))
        {
            if (removed.Contains(track.Index)) continue;
            if (track.IsSubtitle) builder.Subtitle(track.Language, track.LanguageBcp47, track.Name);
            else builder.Video();
        }

        if (removeIndex < 0)
        {
            string? language = null;
            for (var i = arguments.ToList().IndexOf("-o") + 3; i < arguments.Count; i++)
            {
                if (arguments[i] == "--language") language = arguments[++i][2..];
                else if (arguments[i].StartsWith("--", StringComparison.Ordinal)) i++;
                else builder.Subtitle(language == "en" ? "eng" : "por", language);
            }
        }

        var bytes = builder.Build();
        // Saída nunca menor que a entrada (o embedder confere isso).
        var padded = bytes.Concat(new byte[Math.Max(0, (int)new FileInfo(input).Length - bytes.Length)]).ToArray();
        File.WriteAllBytes(output, padded);
        progress?.Report(1);
        return Task.FromResult(new MkvMergeResult(ExitCode, ["#GUI#progress 100%"]));
    }
}

public sealed class SubtitleEmbedTests : IDisposable
{
    private readonly TempLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly LibraryAnalyzer _organizer;
    private readonly FakeSubtitleProvider _provider = new("Fake");
    private readonly SubtitleAnalyzer _analyzer;
    private readonly FakeMkvMergeRunner _runner = new();
    private readonly List<string> _trash = [];
    private readonly string _tool;
    private long? _freeSpace = long.MaxValue;
    private readonly SubtitleEmbedder _embedder;
    private readonly SubtitleDownloader _downloader;

    public SubtitleEmbedTests()
    {
        _settings = new SettingsStore(_library.Data);
        _settings.Update(s =>
        {
            s.TmdbApiKey = "test-key";
            s.SubtitleLanguages = ["pt-BR", "en"];
            s.SubtitleOutput = SubtitleOutput.Embed;
        });
        _history = new HistoryStore(_library.Data);
        var processed = new ProcessedStore(_library.Data);
        var client = new TmdbClient(new HttpClient(_tmdb), () => _settings.Current.TmdbApiKey);
        _organizer = new LibraryAnalyzer(new MediaMatcher(client), new RenamePlanner(client), _settings, processed);
        var providers = new SubtitleProviderSet([_provider], TimeProvider.System);
        _analyzer = new SubtitleAnalyzer(client, _settings, providers);

        _tool = Path.Combine(_library.Base, "tools", MkvMergeLocator.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(_tool)!);
        File.WriteAllBytes(_tool, [0]);
        _embedder = new SubtitleEmbedder(_runner, () => _tool, Trash, _ => _freeSpace);
        _downloader = new SubtitleDownloader(providers, _history, processed, _settings, _organizer, _embedder);

        _tmdb.Show(1, "Show", "Show", "en").Route("tv/1/external_ids", """{"imdb_id":"tt0000001"}""");
        _settings.SetMatch("Series/Show", 1);
        _provider.Has(1, "pt-BR", hash: true).Has(1, "en", hash: true);
    }

    public void Dispose() => _library.Dispose();

    /// <summary>Lixeira simulada: move para fora da biblioteca, como a real.</summary>
    private void Trash(string path)
    {
        _trash.Add(path);
        var target = Path.Combine(_library.Base, "trash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(path, target);
    }

    private string Video(string name = "Show S01E01 - One.mkv") =>
        new MkvBuilder().Video().WriteTo(Path.Combine(_library.Root, "Show", name));

    private async Task<FolderSubtitlePlan> AnalyzeAsync() =>
        await _analyzer.AnalyzeFolderAsync(Assert.Single(_organizer.Scan(_library.Root, MediaKind.Series)), false, TestContext.Current.CancellationToken);

    private async Task<ExecutionResult> DownloadAsync(SubtitleOutput output = SubtitleOutput.Embed)
    {
        var plan = await AnalyzeAsync();
        var downloads = plan.Videos.SelectMany(v => v.Items.Select(i => new SubtitleDownload(plan, v.Query!, i.Best!))).ToList();
        return await _downloader.ApplyAsync(_library.Root, MediaKind.Series, "Legendas", downloads, output, ct: TestContext.Current.CancellationToken);
    }

    private IEnumerable<string> VisibleFiles => _library.Files().Where(f => !f.Contains(".mediaorganizer", StringComparison.Ordinal));

    [Fact]
    public async Task EmbedsAllLanguagesInOnePassAndKeepsABackup()
    {
        var video = Video();

        var result = await DownloadAsync();

        Assert.Equal((2, 0), (result.Succeeded, result.Failed));
        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["--language", "0:pt-BR", "--track-name", "0:Português (Brasil)", "--default-track-flag", "0:no", "--sub-charset", "0:UTF-8"],
            call.SkipWhile(a => a != "--language").Take(8));
        Assert.Contains("0:en", call);
        Assert.Contains("0:English", call);

        Assert.Equal(["Show/Show S01E01 - One.mkv"], VisibleFiles); // nenhum .srt na pasta
        Assert.Equal(["por", "eng"], MatroskaReader.ReadTracks(video).Where(t => t.IsSubtitle).Select(t => t.Language));

        var op = Assert.Single(result.Journal.Operations);
        Assert.Equal((OperationKind.Embed, OperationState.Done, "pt-BR, en"), (op.Kind, op.State, op.Note));
        Assert.Equal([1, 2], op.AddedTrackIds);
        Assert.True(File.Exists(op.From));
        Assert.Single(MatroskaReader.ReadTracks(op.From)); // o backup é o original
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_library.Root, "Show", ".mediaorganizer", "tmp")));
    }

    [Fact]
    public async Task UndoRestoresTheOriginalFromBackup()
    {
        var video = Video();
        var result = await DownloadAsync();

        new RenameExecutor(_history, Trash, _embedder).Undo(result.Journal);

        Assert.Single(MatroskaReader.ReadTracks(video));
        Assert.False(Directory.Exists(Path.Combine(_library.Root, "Show", ".mediaorganizer", "originals")));
        Assert.Single(_trash); // o vídeo com as faixas
    }

    [Fact]
    public async Task WithoutBackupTheOriginalGoesToTrashAndUndoRemuxes()
    {
        _settings.Update(s => s.KeepOriginalAfterEmbed = false);
        var video = Video();
        var result = await DownloadAsync();
        var op = Assert.Single(result.Journal.Operations);
        Assert.Equal("", op.From);
        Assert.Single(_trash);

        new RenameExecutor(_history, Trash, _embedder).Undo(result.Journal);

        Assert.Equal(["--subtitle-tracks", "!1,2"], _runner.Calls[^1].SkipWhile(a => a != "--subtitle-tracks").Take(2));
        Assert.Single(MatroskaReader.ReadTracks(video));
    }

    [Fact]
    public async Task OutputMissingOriginalTracksIsDiscarded()
    {
        var video = Video();
        var before = File.ReadAllBytes(video);
        _runner.DropFirstTrack = true;

        var result = await DownloadAsync();

        Assert.Equal(OperationState.Failed, Assert.Single(result.Journal.Operations).State);
        Assert.Contains("faixas do original", result.Errors[0], StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(video));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_library.Root, "Show", ".mediaorganizer", "tmp")));
    }

    [Fact]
    public async Task MkvMergeErrorLeavesTheVideoAlone()
    {
        var video = Video();
        var before = File.ReadAllBytes(video);
        _runner.ExitCode = 2;

        var result = await DownloadAsync();

        Assert.Contains("Algo deu errado", result.Errors[0], StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(video));
    }

    [Fact]
    public async Task NotEnoughSpaceFailsWithoutRunningMkvMerge()
    {
        Video();
        _freeSpace = 1;

        var result = await DownloadAsync();

        Assert.Contains("Espaço insuficiente", result.Errors[0], StringComparison.Ordinal);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Mp4StaysAsHiddenFiles()
    {
        _library.File("Show/Show S01E01 - One.mp4");

        var result = await DownloadAsync();

        Assert.Equal([OperationKind.Create, OperationKind.Create], result.Journal.Operations.Select(o => o.Kind));
        Assert.Contains("Show/Show S01E01 - One.pt-BR.srt", VisibleFiles);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task EmbedsExistingSubtitlesAndUndoPutsThemBack()
    {
        var video = Video();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var srt = Path.Combine(_library.Root, "Show", "Show S01E01 - One.pt-BR.srt");
        File.WriteAllBytes(srt, Encoding.GetEncoding(1252).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nAção\n"));
        var plan = await AnalyzeAsync();
        var state = Assert.Single(plan.Videos).State;

        var result = await _downloader.EmbedExistingAsync(_library.Root, MediaKind.Series, "Embutir", [new ExistingEmbed(plan, video, state.Existing)],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(["Show/Show S01E01 - One.mkv"], VisibleFiles);
        Assert.Equal([OperationKind.Embed, OperationKind.Move], result.Journal.Operations.Select(o => o.Kind));

        new RenameExecutor(_history, Trash, _embedder).Undo(result.Journal);

        Assert.True(File.Exists(srt));
        Assert.Single(MatroskaReader.ReadTracks(video));
    }

    [Fact]
    public async Task ReportsTheToolVersion()
    {
        Assert.Equal("mkvmerge v80.0 ('Roundabout') 64-bit", await _embedder.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Contains("não encontrado", await new SubtitleEmbedder(_runner, () => Path.Combine(_library.Base, "nope")).GetVersionAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsTrackArgumentsWithFlags()
    {
        var args = SubtitleEmbedder.TrackArguments(new EmbedTrack("x.srt", new SubtitleLanguage("en", Forced: true))).ToList();

        Assert.Equal(["--language", "0:en", "--track-name", "0:English (Forçada)", "--default-track-flag", "0:no",
            "--forced-display-flag", "0:yes", "--sub-charset", "0:UTF-8", "x.srt"], args);
    }

    /// <summary>Com o MKVToolNix instalado (o CI do Ubuntu instala), embute de verdade e confere as faixas.</summary>
    [Fact]
    public async Task RealMkvMergeEmbedsASubtitle()
    {
        var tool = MkvMergeLocator.Find(null);
        if (tool is null)
        {
            Assert.Skip("mkvmerge não instalado.");
            return;
        }

        var dir = Path.Combine(_library.Base, "real");
        Directory.CreateDirectory(dir);
        var first = Path.Combine(dir, "a.srt");
        var second = Path.Combine(dir, "b.srt");
        File.WriteAllText(first, "1\n00:00:01,000 --> 00:00:02,000\nOi\n");
        File.WriteAllText(second, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var video = Path.Combine(dir, "base.mkv");
        var runner = new MkvMergeRunner();
        var created = await runner.RunAsync(tool, ["-o", video, "--language", "0:pt-BR", first], null, TestContext.Current.CancellationToken);
        Assert.True(created.Succeeded, created.ErrorText);

        var embedder = new SubtitleEmbedder(runner, () => tool, Trash);
        var result = await embedder.EmbedAsync(video, [new EmbedTrack(second, new SubtitleLanguage("en"))], keepOriginal: true, null,
            TestContext.Current.CancellationToken);

        var tracks = MatroskaReader.ReadTracks(video);
        Assert.Equal(2, tracks.Count);
        Assert.Equal("en", SubtitleLanguages.Normalize(tracks[1].LanguageBcp47 ?? tracks[1].Language));
        Assert.Equal("English", tracks[1].Name);
        Assert.Equal([1], result.AddedTrackIds);
    }
}
