using System.Net;
using System.Text;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

public sealed class SubtitleInfrastructureTests : IDisposable
{
    private readonly TempLibrary _library = new();

    public SubtitleInfrastructureTests() => ProviderHttp.Delay = (_, _) => Task.CompletedTask;

    public void Dispose() => _library.Dispose();

    private static string HashOf(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return OpenSubtitlesHash.Compute(stream);
    }

    [Fact]
    public void HashOfZerosIsTheFileSize()
    {
        Assert.Equal("0000000000020000", HashOf(new byte[128 * 1024]));
        Assert.Equal("0000000000000064", HashOf(new byte[100])); // menor que 64 KB: blocos se sobrepõem
    }

    [Fact]
    public void HashSumsLittleEndianBlocksOfHeadAndTail()
    {
        // Cada bloco de 8 bytes vale 1 (little-endian): 8192 blocos no início + 8192 no fim.
        var ones = new byte[128 * 1024];
        for (var i = 0; i < ones.Length; i += 8) ones[i] = 1;
        Assert.Equal((131072 + 2 * 8192).ToString("x16", System.Globalization.CultureInfo.InvariantCulture), HashOf(ones));

        // Cada bloco vale ulong.MaxValue (= -1): a soma estoura e subtrai 1 por bloco.
        var max = Enumerable.Repeat((byte)0xFF, 128 * 1024).ToArray();
        Assert.Equal((131072 - 2 * 8192).ToString("x16", System.Globalization.CultureInfo.InvariantCulture), HashOf(max));
    }

    [Fact]
    public void HashOfMissingFileIsNull() => Assert.Null(OpenSubtitlesHash.TryCompute(Path.Combine(_library.Root, "nope.mkv")));

    [Fact]
    public async Task ReadsImdbIdsFromTmdb()
    {
        var tmdb = new FakeTmdb()
            .Route("tv/1399/external_ids", """{"imdb_id":"tt0944947","tvdb_id":121361}""")
            .Route("movie/27205?language=en-US", """{"id":27205,"title":"Inception","original_title":"Inception","imdb_id":"tt1375666"}""");
        var client = new TmdbClient(new HttpClient(tmdb), () => "key");

        var tv = await client.GetTvExternalIdsAsync(1399, TestContext.Current.CancellationToken);
        var movie = await client.GetMovieAsync(27205, LanguagePolicy.English, TestContext.Current.CancellationToken);

        Assert.Equal(new ExternalIds("tt0944947", 121361), tv);
        Assert.Equal("tt1375666", movie?.ImdbId);
    }

    [Fact]
    public void UndoOfCreatedFileSendsItToTrash()
    {
        var subtitle = _library.File("Show/Show S01E01.pt-BR.srt");
        var trashed = new List<string>();
        var history = new HistoryStore(_library.Data);
        var journal = new RenameJournal
        {
            Id = RenameJournal.NewId(),
            CreatedAt = DateTimeOffset.Now,
            LibraryPath = _library.Root,
            Kind = MediaKind.Series,
            Description = "Legendas",
            Operations = [new JournalOperation { Kind = OperationKind.Create, From = "opensubtitles:1", To = subtitle, State = OperationState.Done }],
        };

        var result = new RenameExecutor(history, trashed.Add).Undo(journal);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal([subtitle], trashed);
        Assert.Equal(OperationState.Undone, journal.Operations[0].State);
    }

    [Fact]
    public void JournalsFromV11LoadAsMoves()
    {
        var file = Path.Combine(_library.Data.HistoryDirectory, "old.json");
        Directory.CreateDirectory(_library.Data.HistoryDirectory);
        File.WriteAllText(file, """
            {"id":"old","createdAt":"2026-10-19T23:51:01-03:00","libraryPath":"E:\\Series","kind":"Series","description":"Dark",
             "operations":[{"from":"a.mkv","to":"b.mkv","isDirectory":false,"state":"Done"}]}
            """);

        var journal = Assert.Single(new HistoryStore(_library.Data).List());
        Assert.Equal(OperationKind.Move, journal.Operations[0].Kind);
        Assert.True(journal.CanUndo);
    }

    [Fact]
    public void SettingsFromV11GetSubtitleDefaults()
    {
        File.WriteAllText(_library.Data.SettingsFile, """{"tmdbApiKey":"abc","renameSubtitles":true}""");

        var settings = new SettingsStore(_library.Data).Current;

        Assert.Equal(["pt-BR", "en"], settings.SubtitleLanguages);
        Assert.True(settings.HideSubtitleFiles);
        Assert.True(settings.CheckEmbeddedSubtitles);
        Assert.Equal(SubtitleOutput.File, settings.SubtitleOutput);
        Assert.Equal(HearingImpairedPreference.Avoid, settings.SubtitleHearingImpaired);
        Assert.True(settings.OpenSubtitles.Enabled);
        Assert.Null(settings.OpenSubtitles.ApiKey);
    }

    [Theory]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("PT_BR", "pt-BR")]
    [InlineData("pob", "pt-BR")]
    [InlineData("EN", "en")]
    [InlineData("eng", "en")]
    [InlineData("en-US", "en")]
    [InlineData("English", "en")]
    [InlineData("por", "pt")]
    [InlineData("es-MX", "es-MX")]
    public void NormalizesLanguageCodes(string input, string expected) => Assert.Equal(expected, SubtitleLanguages.Normalize(input));

    [Fact]
    public async Task RetriesServerErrorsButNotQuota429()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        using var ok = await ProviderHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, "x"), "Teste", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, handler.Calls);

        var quota = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var http2 = new HttpClient(quota) { BaseAddress = new Uri("https://example.test/") };
        using var limited = await ProviderHttp.SendAsync(http2, () => new HttpRequestMessage(HttpMethod.Get, "x"), "Teste", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); // sem Retry-After: não repete
        Assert.Equal(1, quota.Calls);
    }

    private sealed class SequenceHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("MediaOrganizer", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            var code = codes[Math.Min(Calls++, codes.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
