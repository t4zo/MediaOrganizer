using System.IO.Compression;
using System.Net;
using System.Text;
using MediaOrganizer.Core.State;
using MediaOrganizer.Core.Subtitles;
using MediaOrganizer.Core.Subtitles.Providers;

namespace MediaOrganizer.Core.Tests;

public sealed class SubDlProviderTests : IDisposable
{
    private const string Search = SubDlProvider.ApiUrl + "?api_key=sd-key&type=tv&imdb_id=tt0944947&languages=BR_PT%2CEN&releases=1&hi=1&unpack=1&subs_per_page=30";

    private static readonly SubtitleLanguage PtBr = new("pt-BR");
    private static readonly SubtitleLanguage En = new("en");

    private readonly FakeHttp _http = new();
    private readonly SubDlSettings _settings = new() { ApiKey = "sd-key" };

    public SubDlProviderTests() => ProviderHttp.Delay = (_, _) => Task.CompletedTask;

    public void Dispose() => _http.Dispose();

    private SubDlProvider Provider() => new(new HttpClient(_http), () => _settings);

    private static SubtitleQuery Episode(int episode = 2) =>
        new(MediaKind.Series, "/lib/Show/Show S01E02.mkv", 1000, null, 1399, "tt0944947", 1, episode, "Show.S01E02.1080p.WEB-DL-NTb", [PtBr, En]);

    private static string Results(params string[] items) => $$"""{"status":true,"results":[],"subtitles":[{{string.Join(',', items)}}]}""";

    private static string Item(string url, string language, string release, bool hi = false, int? episode = 2, int? from = 2, int? end = 2,
        bool fullSeason = false, string unpack = "") => $$$"""
        {"release_name":"{{{release}}}","name":"{{{release}}}.zip","lang":"x","language":"{{{language}}}","url":"{{{url}}}",
         "season":1,"episode":{{{Num(episode)}}},"episode_from":{{{Num(from)}}},"episode_end":{{{Num(end)}}},
         "full_season":{{{(fullSeason ? "true" : "false")}}},"hi":{{{(hi ? "true" : "false")}}},
         "releases":["{{{release}}}"]{{{unpack}}}}
        """;

    private static string Num(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task SearchesTheEpisodeByImdbIdAndParsesLanguagesAndFlags()
    {
        _http.Json(HttpMethod.Get, Search + "&season_number=1&episode_number=2", Results(
            Item("/subtitle/1-1.zip", "BR_PT", "Show.S01E02.1080p.WEB-DL-NTb"),
            Item("/subtitle/2-2.zip", "EN", "Show.S01E02.HDTV-LOL", hi: true),
            Item("/subtitle/3-3.zip", "EN", "Show.S01E02.Forced"),
            Item("/subtitle/4-4.zip", "PT", "Show.S01E02.pt-PT")));

        var candidates = await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken);

        Assert.Equal(["/subtitle/1-1.zip", "/subtitle/2-2.zip"], candidates.Select(c => c.ProviderId));
        Assert.Equal(PtBr, candidates[0].Language);
        Assert.Equal(new SubtitleLanguage("en", HearingImpaired: true), candidates[1].Language);
        Assert.All(candidates, c => Assert.True(c.IdMatch));
        Assert.Single(_http.Requests); // achou no episódio: não busca a temporada
    }

    [Fact]
    public async Task FallsBackToSeasonPacksAndPrefersUnpackedFiles()
    {
        _http
            .Json(HttpMethod.Get, Search + "&season_number=1&episode_number=2", """{"status":false,"error":"can't find movie or tv"}""")
            .Json(HttpMethod.Get, Search + "&season_number=1", Results(
                Item("/subtitle/10-10.zip", "BR_PT", "Show.S01.Complete", episode: 0, from: null, end: 0, fullSeason: true,
                    unpack: ""","unpack_files":[{"url":"/subtitle/10-10/s01e01.srt","name":"Show.S01E01.srt","episode":1},{"url":"/subtitle/10-10/s01e02.srt","name":"Show.S01E02.srt","episode":2}]"""),
                Item("/subtitle/11-11.zip", "EN", "Show.S01E01-E05", episode: 0, from: 1, end: 5),
                Item("/subtitle/12-12.zip", "EN", "Show.S01E06-E10", episode: 0, from: 6, end: 10),
                Item("/subtitle/13-13.zip", "EN", "Show.S01E03", episode: 3, from: 3, end: 3)));

        var candidates = await Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken);

        Assert.Equal(["/subtitle/10-10/s01e02.srt", "/subtitle/11-11.zip"], candidates.Select(c => c.ProviderId));
        Assert.False(candidates[0].IsSeasonPack); // arquivo já separado pelo servidor
        Assert.True(candidates[1].IsSeasonPack);
    }

    [Fact]
    public async Task ExtractsTheEpisodeFromASeasonPack()
    {
        var zip = Zip([.. Enumerable.Range(1, 10).Select(e => ($"Show.S01E{e:00}.srt", $"episodio {e}")), ("__MACOSX/._Show.S01E02.srt", "lixo")]);
        _http.Bytes(SubDlProvider.DownloadUrl + "/subtitle/11-11.zip", zip);
        var candidate = new SubtitleCandidate(SubDlProvider.ProviderName, "/subtitle/11-11.zip", En, "pack", false, true, true, 0);

        var file = await Provider().DownloadAsync(candidate, Episode(), TestContext.Current.CancellationToken);

        Assert.Equal("srt", file.Extension);
        Assert.Equal("episodio 2", Encoding.UTF8.GetString(file.Content).TrimStart('﻿'));
    }

    [Fact]
    public async Task DirectFilesAreNotUnzipped()
    {
        _http.Bytes(SubDlProvider.DownloadUrl + "/subtitle/10-10/s01e02.srt", Encoding.UTF8.GetBytes("direto"));
        var candidate = new SubtitleCandidate(SubDlProvider.ProviderName, "/subtitle/10-10/s01e02.srt", PtBr, "x", false, true, false, 0);

        var file = await Provider().DownloadAsync(candidate, Episode(), TestContext.Current.CancellationToken);

        Assert.Equal("direto", Encoding.UTF8.GetString(file.Content));
    }

    [Fact]
    public void ArchiveIgnoresTraversalAndNonSubtitles()
    {
        var zip = Zip(("../evil.srt", "mal"), ("readme.txt", "x"), ("sub/Show.S01E02.ass", "bom"));

        var file = SubtitleArchive.Extract(zip, 1, 2, "Teste");

        Assert.Equal(("ass", "bom"), (file.Extension, Encoding.UTF8.GetString(file.Content).TrimStart('﻿')));
    }

    [Fact]
    public void ArchiveWithoutTheEpisodeFails()
    {
        var zip = Zip(("Show.S01E01.srt", "1"), ("Show.S01E03.srt", "3"));

        var error = Assert.Throws<SubtitleProviderException>(() => SubtitleArchive.Extract(zip, 1, 2, "Teste"));

        Assert.Equal(SubtitleProviderError.InvalidContent, error.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"daily_limit"}""", SubtitleProviderError.QuotaExceeded)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"api_download_limit_exceeded"}""", SubtitleProviderError.QuotaExceeded)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate_limit"}""", SubtitleProviderError.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"Invalid API key"}""", SubtitleProviderError.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, "<html>cloudflare</html>", SubtitleProviderError.Unavailable)]
    public async Task MapsErrors(HttpStatusCode status, string body, SubtitleProviderError expected)
    {
        _http.Json(HttpMethod.Get, SubDlProvider.ApiUrl, body, status);

        var error = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider().SearchAsync(Episode(), TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("sd-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoviesWithoutImdbUseTmdbId()
    {
        _http.Json(HttpMethod.Get, SubDlProvider.ApiUrl, Results());
        var movie = new SubtitleQuery(MediaKind.Movie, "/lib/Film.mkv", 1, null, 550, null, null, null, "Film", [PtBr]);

        await Provider().SearchAsync(movie, TestContext.Current.CancellationToken);

        Assert.Equal(SubDlProvider.ApiUrl + "?api_key=sd-key&type=movie&tmdb_id=550&languages=BR_PT&releases=1&hi=1&unpack=1&subs_per_page=30",
            Assert.Single(_http.Urls));
    }

    [Theory]
    [InlineData("pt-BR", "BR_PT")]
    [InlineData("en", "EN")]
    [InlineData("es-MX", "ES")]
    public void MapsLanguageCodes(string code, string expected) => Assert.Equal(expected, SubDlProvider.LanguageCode(code));
}
