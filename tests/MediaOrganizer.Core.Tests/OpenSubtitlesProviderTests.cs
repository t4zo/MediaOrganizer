using System.Net;
using System.Text;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;
using MediaOrganizer.Core.Subtitles.Providers;

namespace MediaOrganizer.Core.Tests;

public sealed class OpenSubtitlesProviderTests : IDisposable
{
    private const string Api = "https://api.opensubtitles.com/api/v1/";

    private static readonly SubtitleLanguage PtBr = new("pt-BR");
    private static readonly SubtitleLanguage En = new("en");

    private readonly FakeHttp _http = new();
    private readonly OpenSubtitlesSettings _settings = new() { ApiKey = "os-key" };
    private bool _includeMachine;

    public OpenSubtitlesProviderTests() => ProviderHttp.Delay = (_, _) => Task.CompletedTask;

    public void Dispose() => _http.Dispose();

    private OpenSubtitlesProvider Provider() => new(new HttpClient(_http), () => _settings, () => _includeMachine);

    private static SubtitleQuery Episode(string? hash = "8e245d9679d31e12") =>
        new(MediaKind.Series, "/lib/Show/Show S01E02.mkv", 1000, hash, 1399, "tt0944947", 1, 2,
            "Show.S01E02.1080p.WEB-DL.x264-NTb", [PtBr, En]);

    private static string Item(long fileId, string language, string release, bool hash = false, int season = 1, int episode = 2,
        bool hi = false, bool foreign = false, bool ai = false, int downloads = 100) => $$$"""
        {"id":"{{{fileId}}}","type":"subtitle","attributes":{"language":"{{{language}}}","download_count":{{{downloads}}},
         "hearing_impaired":{{{Bool(hi)}}},"foreign_parts_only":{{{Bool(foreign)}}},
         "ai_translated":{{{Bool(ai)}}},"machine_translated":false,"release":"{{{release}}}",
         "moviehash_match":{{{Bool(hash)}}},
         "feature_details":{"season_number":{{{season}}},"episode_number":{{{episode}}},"parent_imdb_id":944947},
         "files":[{"file_id":{{{fileId}}},"file_name":"{{{release}}}.srt"}]}}
        """;

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Results(params string[] items) => $$"""{"total_count":{{items.Length}},"data":[{{string.Join(',', items)}}]}""";

    [Fact]
    public async Task SearchSendsSortedLowercaseParameters()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results());

        await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(
            Api + "subtitles?ai_translated=exclude&episode_number=2&languages=en%2Cpt-br&moviehash=8e245d9679d31e12&parent_imdb_id=944947&season_number=1&type=episode",
            request.Url);
        Assert.Equal("os-key", request.Request.Headers.GetValues("Api-Key").Single());
        Assert.Contains("MediaOrganizer", request.Request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoviesUseTmdbIdWhenThereIsNoImdbId()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results());
        var movie = new SubtitleQuery(MediaKind.Movie, "/lib/Film (2020)/Film (2020).mkv", 1, null, 550, null, null, null, "Film", [PtBr]);

        await Provider().SearchAsync(movie, TestContext.Current.CancellationToken);

        Assert.Equal(Api + "subtitles?ai_translated=exclude&languages=pt-br&tmdb_id=550&type=movie", Assert.Single(_http.Urls));
    }

    [Fact]
    public async Task ParsesCandidatesAndFiltersOtherEpisodesAndLanguages()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results(
            Item(1, "pt-BR", "Show.S01E02.720p.HDTV-LOL", hash: true),
            Item(2, "en", "Show.S01E02.1080p.WEB-DL.x264-NTb", hi: true),
            Item(3, "pt-PT", "Show.S01E02.1080p.WEB-DL.x264-NTb"),
            Item(4, "pt-BR", "Show.S01E03.1080p.WEB-DL.x264-NTb", episode: 3),
            Item(5, "en", "Show.S01E02.Forced", foreign: true)));

        var candidates = await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2"], candidates.Select(c => c.ProviderId));
        Assert.True(candidates[0].HashMatch);
        Assert.True(candidates[0].IdMatch);
        Assert.Equal(new SubtitleLanguage("en", HearingImpaired: true), candidates[1].Language);
    }

    [Fact]
    public async Task ForcedSubtitlesComeOnlyWhenRequested()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results(Item(5, "en", "Show.S01E02.Forced", foreign: true)));
        var query = Episode() with { Languages = [new SubtitleLanguage("en", Forced: true)] };

        var candidate = Assert.Single(await Provider().SearchAsync(query, TestContext.Current.CancellationToken));

        Assert.True(candidate.Language.Forced);
    }

    [Fact]
    public async Task HashMatchOutranksSameRelease()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results(
            Item(1, "pt-BR", "Show.S01E02.1080p.WEB-DL.x264-NTb", downloads: 5000),
            Item(2, "pt-BR", "Other.Release-XYZ", hash: true, downloads: 10)));

        var ranked = SubtitleScorer.Rank(await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken),
            Episode().ReleaseName, HearingImpairedPreference.Avoid);

        Assert.Equal("2", ranked[0].ProviderId);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public async Task MachineTranslatedCanBeIncluded()
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", Results(Item(1, "pt-BR", "x", ai: true)));
        _includeMachine = true;

        var candidate = Assert.Single(await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.True(candidate.MachineTranslated);
        Assert.Contains("machine_translated=include", Assert.Single(_http.Urls), StringComparison.Ordinal);
        Assert.DoesNotContain("ai_translated", Assert.Single(_http.Urls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadLogsInAndFollowsTheLink()
    {
        _settings.Username = "user";
        _settings.Password = "secret";
        _http
            .Json(HttpMethod.Post, Api + "login", """{"token":"jwt","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":100,"remaining_downloads":99}}""")
            .Json(HttpMethod.Post, "https://vip-api.opensubtitles.com/api/v1/download",
                """{"link":"https://dl.opensubtitles.org/file/123","file_name":"x.srt","remaining":98,"reset_time_utc":"2026-10-22T00:00:00Z"}""")
            .Bytes("https://dl.opensubtitles.org/file/123", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nOi\n"));
        var provider = Provider();
        var candidate = new SubtitleCandidate(OpenSubtitlesProvider.ProviderName, "123", PtBr, "x", true, true, false, 1);

        var file = await provider.DownloadAsync(candidate, Episode(), TestContext.Current.CancellationToken);

        Assert.Equal("srt", file.Extension);
        Assert.Contains("Oi", Encoding.UTF8.GetString(file.Content), StringComparison.Ordinal);
        Assert.Equal(98, provider.RemainingDownloads);
        var download = _http.Requests.Single(r => r.Url.EndsWith("/download", StringComparison.Ordinal));
        Assert.Equal("""{"file_id":123}""", download.Body);
        Assert.Equal("Bearer jwt", download.Request.Headers.Authorization?.ToString());
        Assert.Equal("""{"username":"user","password":"secret"}""", _http.Requests[0].Body);
    }

    [Fact]
    public async Task QuotaExceededReportsResetTime()
    {
        _http.Json(HttpMethod.Post, Api + "download",
            """{"requests":20,"remaining":0,"message":"You have downloaded your allowed 20 subtitles for 24h","reset_time_utc":"2026-10-22T03:00:00Z"}""",
            HttpStatusCode.NotAcceptable);
        var candidate = new SubtitleCandidate(OpenSubtitlesProvider.ProviderName, "1", PtBr, "x", false, true, false, 1);
        var provider = Provider();

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => provider.DownloadAsync(candidate, Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(SubtitleProviderError.QuotaExceeded, error.Error);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 3, 0, 0, TimeSpan.Zero), error.ResetAt);
        Assert.Equal(0, provider.RemainingDownloads);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SubtitleProviderError.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, SubtitleProviderError.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, SubtitleProviderError.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, SubtitleProviderError.Unavailable)]
    public async Task MapsSearchErrors(HttpStatusCode status, SubtitleProviderError expected)
    {
        _http.Json(HttpMethod.Get, Api + "subtitles", "{}", status);

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void IsConfiguredNeedsKeyAndEnabled()
    {
        Assert.True(Provider().IsConfigured);
        _settings.Enabled = false;
        Assert.False(Provider().IsConfigured);
        _settings.Enabled = true;
        _settings.ApiKey = "";
        Assert.False(Provider().IsConfigured);
    }

    [Theory]
    [InlineData("tt0944947", "944947")]
    [InlineData("0944947", "944947")]
    [InlineData("tt1375666", "1375666")]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void SanitizesImdbIds(string input, string? expected) => Assert.Equal(expected, OpenSubtitlesProvider.NumericImdbId(input));

    [Fact]
    public void ParsesReleaseInfo()
    {
        Assert.Equal(new ReleaseInfo("NTb", "WEB-DL", "1080p"), ReleaseInfo.Parse("Show.S01E02.1080p.WEB-DL.x264-NTb.mkv"));
        Assert.Equal(new ReleaseInfo("lol", "HDTV", "720p"), ReleaseInfo.Parse("show.s01e02.720p.hdtv.x264-lol"));
        Assert.Equal(new ReleaseInfo(null, "BluRay", "2160p"), ReleaseInfo.Parse("Film 2020 2160p BluRay"));
    }

    [Fact]
    public void AvoidingHearingImpairedPenalizesSdh()
    {
        var normal = new SubtitleCandidate("x", "1", PtBr, "a", false, true, false, 1);
        var sdh = normal with { ProviderId = "2", Language = new SubtitleLanguage("pt-BR", HearingImpaired: true), Downloads = 1000 };

        Assert.Equal("1", SubtitleScorer.Rank([sdh, normal], "a", HearingImpairedPreference.Avoid)[0].ProviderId);
        Assert.Equal("2", SubtitleScorer.Rank([normal, sdh], "a", HearingImpairedPreference.Prefer)[0].ProviderId);
    }
}
