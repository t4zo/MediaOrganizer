using MediaOrganizer.Core.Collection;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.State;

namespace MediaOrganizer.Core.Tests;

public sealed class CollectionTests : IDisposable
{
    private static readonly DateTimeOffset Today = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TempLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();
    private readonly SettingsStore _settings;
    private readonly CollectionAnalyzer _analyzer;

    public CollectionTests()
    {
        _settings = new SettingsStore(_library.Data);
        _settings.Update(s => s.TmdbApiKey = "test-key");
        var client = new TmdbClient(new HttpClient(_tmdb), () => _settings.Current.TmdbApiKey);
        _analyzer = new CollectionAnalyzer(client, _settings, new FixedTime(Today));

        _tmdb
            .ShowWithSeasons(100, "Test Show", "en", 0, 1, 2)
            .SeasonWithDates(100, 1, "en-US", (1, "One", "2020-01-01"), (2, "Two", "2020-01-08"), (3, "Three", "2020-01-15"))
            .SeasonWithDates(100, 2, "en-US", (1, "Four", "2024-01-01"), (2, "Five; the end", "2024-01-08"), (3, "Six", "2024-12-01"), (4, "Seven", null))
            .SeasonWithDates(100, 0, "en-US", (1, "Special", "2021-05-01"));
    }

    public void Dispose() => _library.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private async Task<SeriesReport> AnalyzeAsync()
    {
        _settings.SetMatch("Series/Test Show", 100);
        var scanner = new LibraryScanner([], new JunkDetector([]), includeSpecials: true);
        var folder = Assert.Single(scanner.Scan(_library.Root, MediaKind.Series));
        return await _analyzer.AnalyzeSeriesAsync(folder, refresh: false, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListsMissingAiredEpisodesPerSeason()
    {
        _library.File("Test Show/Test Show S01E01.mkv");
        _library.File("Test Show/Test Show S01E02-E03.mkv"); // multi-episódio conta como dois
        _library.File("Test Show/Test Show S02E01.mkv");

        var report = await AnalyzeAsync();

        Assert.Equal(CollectionStatus.Incomplete, report.Status);
        var s1 = report.Seasons.Single(s => s.Season == 1);
        Assert.Equal((3, 3), (s1.Aired, s1.Present));
        Assert.True(s1.IsComplete);

        var s2 = report.Seasons.Single(s => s.Season == 2);
        Assert.Equal((2, 1), (s2.Aired, s2.Present));
        var missing = Assert.Single(s2.Missing);
        Assert.Equal(new EpisodeRef(2, 2, "Five; the end", new DateOnly(2024, 1, 8)), missing);
        Assert.Equal(2, s2.Upcoming.Count); // futuro e sem data não contam como faltando
        Assert.Equal((5, 4, 1), (report.Aired, report.Present, report.MissingCount));
    }

    [Fact]
    public async Task MissingSpecialsDoNotMakeSeriesIncomplete()
    {
        _library.File("Test Show/Test Show S01E01-E03.mkv");
        _library.File("Test Show/Test Show S02E01.mkv");
        _library.File("Test Show/Test Show S02E02.mkv");

        var report = await AnalyzeAsync();

        Assert.Equal(CollectionStatus.Complete, report.Status);
        Assert.Single(report.Specials!.Missing);
    }

    [Fact]
    public async Task SeriesWithoutMatchDoesNotCallTmdb()
    {
        _library.File("Unknown/Unknown S01E01.mkv");
        var scanner = new LibraryScanner([], new JunkDetector([]));
        var folder = Assert.Single(scanner.Scan(_library.Root, MediaKind.Series));

        var report = await _analyzer.AnalyzeSeriesAsync(folder, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(CollectionStatus.NoMatch, report.Status);
        Assert.Empty(_tmdb.Requests);
    }

    [Fact]
    public async Task ExportsMissingEpisodesAsCsvAndText()
    {
        _library.File("Test Show/Test Show S01E01.mkv");

        var report = await AnalyzeAsync();
        var csv = CollectionExporter.ToCsv([report]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(CollectionExporter.CsvHeader, csv[0]);
        Assert.Equal("Test Show;1;2;Two;2020-01-08", csv[1]);
        Assert.Contains("Test Show;2;2;\"Five; the end\";2024-01-08", csv);
        Assert.Equal(5, csv.Length); // cabeçalho + S01E02, S01E03, S02E01, S02E02 (especiais fora)
        Assert.Contains("S01E02  Two  (08/01/2020)", CollectionExporter.ToText([report]), StringComparison.Ordinal);
    }
}
