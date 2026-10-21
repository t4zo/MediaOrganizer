using System.Text;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

/// <summary>Site de legendas falso: responde por episódio e idioma, conta downloads e pode esgotar a cota.</summary>
public sealed class FakeSubtitleProvider(string name) : ISubtitleProvider
{
    private readonly Dictionary<(int Episode, string Language), List<SubtitleCandidate>> _results = [];

    public string Name { get; } = name;
    public bool IsConfigured { get; set; } = true;
    public int? RemainingDownloads { get; set; }
    public int? QuotaAfter { get; set; }
    public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nOlá\n");
    public List<SubtitleQuery> Searches { get; } = [];
    public List<string> Downloads { get; } = [];

    public FakeSubtitleProvider Has(int episode, string language, bool hash = false, bool idMatch = true, string release = "Show.S01E01.WEB-DL-GRP",
        bool hi = false, bool forced = false)
    {
        var key = (episode, language);
        if (!_results.TryGetValue(key, out var list)) _results[key] = list = [];
        list.Add(new SubtitleCandidate(Name, $"{Name}-{episode}-{language}-{list.Count}", new SubtitleLanguage(language, forced, hi), release, hash, idMatch, false, 10));
        return this;
    }

    public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        Searches.Add(query);
        IReadOnlyList<SubtitleCandidate> found = [.. query.Languages.SelectMany(l => _results.GetValueOrDefault((query.Episode ?? 0, l.Code), []))
            .Where(c => query.Languages.Any(l => l.SameKind(c.Language)))];
        return Task.FromResult(found);
    }

    public Task<SubtitleFile> DownloadAsync(SubtitleCandidate candidate, SubtitleQuery query, CancellationToken ct)
    {
        if (QuotaAfter is { } limit && Downloads.Count >= limit)
            throw new SubtitleProviderException(Name, SubtitleProviderError.QuotaExceeded, $"Cota diária do {Name} esgotada.");
        Downloads.Add(candidate.ProviderId);
        return Task.FromResult(new SubtitleFile(Content, "srt"));
    }
}

public sealed class SubtitleWorkflowTests : IDisposable
{
    private readonly TempLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();
    private readonly SettingsStore _settings;
    private readonly ProcessedStore _processed;
    private readonly HistoryStore _history;
    private readonly LibraryAnalyzer _organizer;
    private readonly FakeSubtitleProvider _first = new("Primeiro");
    private readonly FakeSubtitleProvider _second = new("Segundo");
    private readonly SubtitleProviderSet _providers;
    private readonly SubtitleAnalyzer _analyzer;
    private readonly SubtitleDownloader _downloader;
    private readonly List<string> _trash = [];

    public SubtitleWorkflowTests()
    {
        _settings = new SettingsStore(_library.Data);
        _settings.Update(s =>
        {
            s.TmdbApiKey = "test-key";
            s.SubtitleLanguages = ["pt-BR"];
        });
        _processed = new ProcessedStore(_library.Data);
        _history = new HistoryStore(_library.Data);
        var client = new TmdbClient(new HttpClient(_tmdb), () => _settings.Current.TmdbApiKey);
        _organizer = new LibraryAnalyzer(new MediaMatcher(client), new RenamePlanner(client), _settings, _processed);
        _providers = new SubtitleProviderSet([_first, _second], TimeProvider.System);
        _analyzer = new SubtitleAnalyzer(client, _settings, _providers);
        _downloader = new SubtitleDownloader(_providers, _history, _processed, _settings, _organizer);

        _tmdb
            .Show(1, "Show", "Show", "en")
            .Route("tv/1/external_ids", """{"imdb_id":"tt0000001"}""")
            .Season(1, 1, "en-US", (1, "One"), (2, "Two"), (3, "Three"));
        _settings.SetMatch("Series/Show", 1);
    }

    public void Dispose() => _library.Dispose();

    private string Root => _library.Root;

    private async Task<FolderSubtitlePlan> AnalyzeAsync(bool refresh = false)
    {
        var folder = Assert.Single(_organizer.Scan(Root, MediaKind.Series));
        return await _analyzer.AnalyzeFolderAsync(folder, refresh, TestContext.Current.CancellationToken);
    }

    private static List<SubtitleDownload> Selected(FolderSubtitlePlan plan) =>
        [.. plan.Videos.SelectMany(v => v.Items
            .Where(i => i.Status == SubtitleItemStatus.Found && !i.NeedsConfirmation)
            .Select(i => new SubtitleDownload(plan, v.Query!, i.Best!)))];

    private Task<ExecutionResult> ApplyAsync(FolderSubtitlePlan plan) =>
        _downloader.ApplyAsync(Root, MediaKind.Series, "Legendas: Show", Selected(plan), ct: TestContext.Current.CancellationToken);

    [Fact]
    public async Task OnlyVideosWithoutTheLanguageAreSearched()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        _library.File("Show/Show S01E01 - One.pt-BR.srt");
        new MkvBuilder().Video().Subtitle("por", "pt-BR").WriteTo(Path.Combine(Root, "Show", "Show S01E02 - Two.mkv"));
        _library.File("Show/Show S01E03 - Three.mkv");
        _first.Has(3, "pt-BR", hash: true);

        var plan = await AnalyzeAsync();

        Assert.Equal(FolderSubtitleStatus.Missing, plan.Status);
        var item = Assert.Single(plan.Items);
        Assert.EndsWith("Show S01E03 - Three.mkv", item.VideoPath, StringComparison.Ordinal);
        Assert.Equal(SubtitleItemStatus.Found, item.Status);
        Assert.False(item.NeedsConfirmation);
        var search = Assert.Single(_first.Searches);
        Assert.Equal(("tt0000001", 1, 3), (search.ImdbId, search.Season, search.Episode));
        Assert.NotNull(search.FileHash);
    }

    [Fact]
    public async Task EnglishIsSearchedForEveryVideoWithoutIt()
    {
        _settings.Update(s => s.SubtitleLanguages = ["pt-BR", "en"]);
        _library.File("Show/Show S01E01 - One.mkv");
        _library.File("Show/Show S01E01 - One.pt-BR.srt");
        _library.File("Show/Show S01E02 - Two.mkv");
        _first.Has(1, "en").Has(2, "en").Has(2, "pt-BR");

        var plan = await AnalyzeAsync();
        var result = await ApplyAsync(plan);

        Assert.Equal(3, result.Succeeded);
        Assert.Equal([("pt-BR", 1), ("en", 2)], plan.MissingByLanguage.OrderByDescending(x => x.Language, StringComparer.Ordinal));
        Assert.Contains("Show/Show S01E01 - One.en.srt", _library.Files());
        Assert.Contains("Show/Show S01E02 - Two.en.srt", _library.Files());
        Assert.Contains("Show/Show S01E02 - Two.pt-BR.srt", _library.Files());
    }

    [Fact]
    public async Task FirstAvailableFallsBackToEnglish()
    {
        _settings.Update(s =>
        {
            s.SubtitleLanguages = ["pt-BR", "en"];
            s.SubtitleLanguageMode = SubtitleLanguageMode.FirstAvailable;
        });
        _library.File("Show/Show S01E01 - One.mkv");
        _first.Has(1, "en");

        var item = Assert.Single((await AnalyzeAsync()).Items);

        Assert.Equal("en", item.Wanted.Code);
        Assert.Equal(SubtitleItemStatus.Found, item.Status);
        Assert.Equal("Sem legenda pt-BR; usando en.", item.Message);
    }

    [Fact]
    public async Task LaterProvidersAreAskedOnlyForWhatIsStillMissing()
    {
        _settings.Update(s => s.SubtitleLanguages = ["pt-BR", "en"]);
        _library.File("Show/Show S01E01 - One.mkv");
        _first.Has(1, "pt-BR", hash: true);
        _second.Has(1, "en");

        var plan = await AnalyzeAsync();

        Assert.Equal(["en"], Assert.Single(_second.Searches).Languages.Select(l => l.Code));
        Assert.Equal(["Primeiro", "Segundo"], plan.Items.Select(i => i.Best!.Provider));
    }

    [Fact]
    public async Task DownloadIsJournaledHiddenAndUndoable()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        _first.Has(1, "pt-BR", hash: true);

        var result = await ApplyAsync(await AnalyzeAsync());

        var target = Path.Combine(Root, "Show", "Show S01E01 - One.pt-BR.srt");
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("Olá", File.ReadAllText(target, Encoding.UTF8).Split('\n')[2]);
        if (HiddenFile.IsSupported) Assert.True(HiddenFile.IsHidden(target));

        var journal = Assert.Single(_history.List());
        var op = Assert.Single(journal.Operations);
        Assert.Equal((OperationKind.Create, "Primeiro:Primeiro-1-pt-BR-0", target, OperationState.Done), (op.Kind, op.From, op.To, op.State));

        new RenameExecutor(_history, _trash.Add).Undo(journal);
        Assert.Equal([target], _trash);
    }

    [Fact]
    public async Task VisibleWhenHidingIsOff()
    {
        _settings.Update(s => s.HideSubtitleFiles = false);
        _library.File("Show/Show S01E01 - One.mkv");
        _first.Has(1, "pt-BR", hash: true);

        await ApplyAsync(await AnalyzeAsync());

        Assert.False(HiddenFile.IsHidden(Path.Combine(Root, "Show", "Show S01E01 - One.pt-BR.srt")));
    }

    [Fact]
    public async Task Windows1252IsSavedAsUtf8()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        _first.Has(1, "pt-BR", hash: true);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _first.Content = Encoding.GetEncoding(1252).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nAção, “citação” e €\n");

        await ApplyAsync(await AnalyzeAsync());

        var bytes = File.ReadAllBytes(Path.Combine(Root, "Show", "Show S01E01 - One.pt-BR.srt"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("Ação, “citação” e €", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTargetIsAConflictAndIsNotDownloaded()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        _library.File("Show/Show S01E01 - One.pt-BR.sdh.srt", 3); // SDH conta como a normal
        _library.File("Show/Show S01E02 - Two.mkv");
        _library.File("Show/Show S01E02 - Two.pt-BR.forced.srt", 3);
        _settings.Update(s => s.DownloadForcedSubtitles = true);
        _first.Has(2, "pt-BR", hash: true, forced: true).Has(1, "pt-BR", forced: true, hash: true);

        var plan = await AnalyzeAsync();

        // E01 só precisa da forçada; E02 precisa da normal (a forçada já existe).
        Assert.Equal(["Show S01E01 - One.mkv:pt-BR.forced", "Show S01E02 - Two.mkv:pt-BR"],
            plan.Items.Select(i => $"{Path.GetFileName(i.VideoPath)}:{i.Wanted}"));

        File.WriteAllText(Path.Combine(Root, "Show", "Show S01E01 - One.pt-BR.forced.srt"), "x");
        var conflicted = await AnalyzeAsync();
        Assert.DoesNotContain(conflicted.Items, i => i.Status == SubtitleItemStatus.Found && i.Wanted.Forced);
    }

    [Fact]
    public async Task QuotaMidBatchFailsTheRestOfThatProviderOnly()
    {
        _settings.Update(s => s.SubtitleLanguages = ["pt-BR", "en"]);
        _library.File("Show/Show S01E01 - One.mkv");
        _library.File("Show/Show S01E02 - Two.mkv");
        _library.File("Show/Show S01E03 - Three.mkv");
        _first.Has(1, "pt-BR", hash: true).Has(2, "pt-BR", hash: true).Has(3, "pt-BR", hash: true);
        _second.Has(1, "en").Has(2, "en").Has(3, "en");
        _first.QuotaAfter = 1;

        var result = await ApplyAsync(await AnalyzeAsync());

        Assert.Equal(4, result.Succeeded); // 1 do primeiro + 3 do segundo
        Assert.Equal(2, result.Failed);
        Assert.Single(_first.Downloads); // depois da cota, não tenta de novo
        Assert.NotNull(_providers.DisabledReason(_first));
        Assert.All(result.Journal.Operations.Where(o => o.State == OperationState.Failed), o => Assert.Contains("Cota", o.Error, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoubtfulItemsStartUnselected()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        _library.File("Show/Show S01E01 - One.pt.srt");
        _library.File("Show/Show S01E02 - Two.mkv");
        _first.Has(1, "pt-BR", hash: true).Has(2, "pt-BR", idMatch: false);

        var items = (await AnalyzeAsync()).Items.ToList();

        Assert.All(items, i => Assert.True(i.NeedsConfirmation));
        Assert.Contains("pt", items[0].Message, StringComparison.Ordinal);
        Assert.Contains("sincronia", items[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnidentifiedFolderDoesNotSearch()
    {
        _library.File("Other/Other S01E01.mkv");
        var folder = Assert.Single(_organizer.Scan(Root, MediaKind.Series));

        var plan = await _analyzer.AnalyzeFolderAsync(folder, false, TestContext.Current.CancellationToken);

        Assert.Equal(FolderSubtitleStatus.NoMatch, plan.Status);
        Assert.Empty(_first.Searches);
    }

    [Fact]
    public async Task NoConfiguredProviderReportsWhy()
    {
        _first.IsConfigured = false;
        _second.IsConfigured = false;
        _library.File("Show/Show S01E01 - One.mkv");

        var item = Assert.Single((await AnalyzeAsync()).Items);

        Assert.Equal(SubtitleItemStatus.Error, item.Status);
        Assert.Contains("Configurações", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessedFolderStaysProcessedAfterDownload()
    {
        _library.File("Show/Show S01E01 - One.mkv");
        var folder = Assert.Single(_organizer.Scan(Root, MediaKind.Series));
        _processed.MarkProcessed(folder, 1);
        _first.Has(1, "pt-BR", hash: true);

        await ApplyAsync(await AnalyzeAsync());

        var rescanned = Assert.Single(_organizer.Scan(Root, MediaKind.Series));
        Assert.Single(rescanned.Subtitles);
        Assert.True(_processed.IsUnchanged(rescanned));
    }

    [Fact]
    public async Task MoviesUseTheMovieImdbId()
    {
        _tmdb.Route("movie/550?language=en-US", """{"id":550,"title":"Fight Club","original_title":"Fight Club","imdb_id":"tt0137523","release_date":"1999-10-15"}""");
        var movies = Path.Combine(_library.Base, "Movies");
        Directory.CreateDirectory(Path.Combine(movies, "Fight Club (1999)"));
        File.WriteAllBytes(Path.Combine(movies, "Fight Club (1999)", "Fight Club (1999).mkv"), [1]);
        _settings.SetMatch("Movie/Fight Club (1999)", 550);
        _first.Has(0, "pt-BR", hash: true);

        var folder = Assert.Single(_organizer.Scan(movies, MediaKind.Movie));
        var plan = await _analyzer.AnalyzeFolderAsync(folder, false, TestContext.Current.CancellationToken);

        Assert.Equal(SubtitleItemStatus.Found, Assert.Single(plan.Items).Status);
        Assert.Equal(("tt0137523", (int?)null), (_first.Searches[0].ImdbId, _first.Searches[0].Episode));
    }
}
