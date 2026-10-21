using System.IO.Compression;
using System.Net;
using System.Text;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;
using MediaOrganizer.Core.Subtitles.Providers;

namespace MediaOrganizer.Core.Tests;

public sealed class SubSourceProviderTests : IDisposable
{
    private const string Api = SubSourceProvider.ApiUrl;

    private static readonly SubtitleLanguage PtBr = new("pt-BR");
    private static readonly SubtitleLanguage En = new("en");

    private readonly FakeHttp _http = new();
    private readonly SubSourceSettings _settings = new() { ApiKey = "ss-key" };

    public SubSourceProviderTests() => ProviderHttp.Delay = (_, _) => Task.CompletedTask;

    public void Dispose() => _http.Dispose();

    private SubSourceProvider Provider() => new(new HttpClient(_http), () => _settings);

    private static SubtitleQuery Episode() =>
        new(MediaKind.Series, "/lib/Show/Show S02E03.mkv", 1000, null, 1399, "tt0944947", 2, 3, "Show.S02E03.1080p.WEB-DL-NTb", [PtBr, En]);

    private static string Sub(int id, string language, string release, bool hi = false, bool foreign = false, string commentary = "") => $$$"""
        {"subtitleId":{{{id}}},"language":"{{{language}}}","releaseInfo":["{{{release}}}"],"hearingImpaired":{{{(hi ? "true" : "false")}}},
         "foreignParts":{{{(foreign ? "true" : "false")}}},"commentary":"{{{commentary}}}","link":"/subtitle/x/{{{id}}}","downloads":{{{id * 10}}}}
        """;

    private void Titles() =>
        _http.Json(HttpMethod.Get, Api + "movies/search?api_key=ss-key&searchType=imdb&imdb=tt0944947&season=2",
            """{"success":true,"data":[{"movieId":111,"title":"Show","season":1},{"movieId":222,"title":"Show","season":2}]}""");

    [Fact]
    public async Task FindsTheSeasonTitleThenSearchesEachLanguage()
    {
        Titles();
        _http
            .Json(HttpMethod.Get, Api + "subtitles?api_key=ss-key&language=brazilian_portuguese&limit=100&movieId=222&seasonNumber=2&episodeNumber=3",
                $$"""{"success":true,"data":[{{Sub(1, "brazilian_portuguese", "Show.S02E03.1080p.WEB-DL-NTb")}},{{Sub(2, "brazilian_portuguese", "Show.S02E04.720p")}}]}""")
            .Json(HttpMethod.Get, Api + "subtitles?api_key=ss-key&language=english&limit=100&movieId=222&seasonNumber=2&episodeNumber=3",
                $$"""{"success":true,"data":[{{Sub(3, "english", "Show.S02E03.HDTV", commentary: "SDH")}},{{Sub(4, "english", "Show.S02E03", foreign: true)}},{{Sub(5, "english", "Show.S02.Complete")}}]}""");

        var candidates = await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken);

        Assert.Equal(["1", "3", "5"], candidates.Select(c => c.ProviderId));
        Assert.Equal(PtBr, candidates[0].Language);
        Assert.True(candidates[1].Language.HearingImpaired);
        Assert.True(candidates[2].IsSeasonPack);
    }

    [Fact]
    public async Task WithoutImdbIdThereIsNothingToSearch()
    {
        var candidates = await Provider().SearchAsync(Episode() with { ImdbId = null }, TestContext.Current.CancellationToken);

        Assert.Empty(candidates);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task DownloadsAndUnzips()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Show.S02E03.srt").Open(), Encoding.UTF8);
            writer.Write("legenda");
        }
        _http.Bytes(Api + "subtitles/1/download?api_key=ss-key", buffer.ToArray());
        var candidate = new SubtitleCandidate(SubSourceProvider.ProviderName, "1", PtBr, "x", false, true, false, 0);

        var file = await Provider().DownloadAsync(candidate, Episode(), TestContext.Current.CancellationToken);

        Assert.Equal("legenda", Encoding.UTF8.GetString(file.Content).TrimStart('﻿'));
    }

    [Fact]
    public async Task LongRateLimitIsTreatedAsQuota()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(5);
        _http.On(HttpMethod.Get, Api + "movies/search", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add("X-RateLimit-Reset", reset.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(SubtitleProviderError.QuotaExceeded, error.Error);
        Assert.Equal(reset.ToUnixTimeSeconds(), error.ResetAt?.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ShortRateLimitIsJustRateLimited()
    {
        _http.On(HttpMethod.Get, Api + "movies/search", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(SubtitleProviderError.RateLimited, error.Error);
    }

    [Fact]
    public async Task RejectedKeyIsAnAuthenticationError()
    {
        _http.Json(HttpMethod.Get, Api + "movies/search", """{"success":false}""", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(SubtitleProviderError.AuthenticationFailed, error.Error);
        Assert.DoesNotContain("ss-key", error.Message, StringComparison.Ordinal);
    }
}
