using System.Globalization;
using System.Text;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

/// <summary>Legendas sintéticas: falas de 1 a 4 s com pausas de 0,2 a 6 s, como num episódio de ~45 min.</summary>
internal static class SyntheticSubtitles
{
    public static List<SubtitleCue> Episode(int seed, int count = 600)
    {
        var random = new Random(seed);
        var cues = new List<SubtitleCue>();
        var time = 5.0;
        for (var i = 0; i < count; i++)
        {
            time += 0.2 + random.NextDouble() * 5.8;
            var duration = 1 + random.NextDouble() * 3;
            cues.Add(new SubtitleCue(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(time + duration)));
            time += duration;
        }
        return cues;
    }

    /// <summary>Mesmas falas, como numa tradução: bordas um pouco diferentes e algumas falas juntadas.</summary>
    public static List<SubtitleCue> Translation(IReadOnlyList<SubtitleCue> source, int seed)
    {
        var random = new Random(seed);
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < source.Count; i++)
        {
            var start = source[i].Start;
            var end = source[i].End;
            if (i + 1 < source.Count && random.NextDouble() < 0.1 && (source[i + 1].Start - end).TotalSeconds < 1) end = source[++i].End;
            cues.Add(new SubtitleCue(start + Jitter(random), end + Jitter(random)));
        }
        return cues;
    }

    public static List<SubtitleCue> Shift(IEnumerable<SubtitleCue> cues, double seconds) =>
        [.. cues.Select(c => new SubtitleCue(c.Start + TimeSpan.FromSeconds(seconds), c.End + TimeSpan.FromSeconds(seconds)))];

    public static List<SubtitleCue> Scale(IEnumerable<SubtitleCue> cues, double factor) =>
        [.. cues.Select(c => new SubtitleCue(c.Start * factor, c.End * factor))];

    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var text = new StringBuilder();
        var n = 1;
        foreach (var cue in cues)
        {
            text.Append(CultureInfo.InvariantCulture, $"{n++}\r\n{Time(cue.Start)} --> {Time(cue.End)}\r\nFala {n}\r\n\r\n");
        }
        return text.ToString();
    }

    private static string Time(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private static TimeSpan Jitter(Random random) => TimeSpan.FromMilliseconds(random.Next(-150, 151));
}

public sealed class SubtitleCuesTests
{
    [Fact]
    public void ParsesSrtIgnoringTagsAndEmptyCues()
    {
        var cues = SubtitleCues.Parse("""
            1
            00:00:01,000 --> 00:00:02,500
            <i>Olá</i>

            2
            00:00:03,000 --> 00:00:04,000
            <font color="red"></font>

            3
            01:02:03,450 --> 01:02:05,000 X1:10
            Tchau
            """);

        Assert.Equal([
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)),
            new SubtitleCue(new TimeSpan(0, 1, 2, 3, 450), new TimeSpan(0, 1, 2, 5)),
        ], cues);
    }

    [Fact]
    public void ParsesVttWithoutHours()
    {
        var cues = SubtitleCues.Parse("WEBVTT\n\nNOTE comentário\n\n00:01.000 --> 00:02.000 align:start\nOi\n\n01:00:00.000 --> 01:00:01.500\nTudo bem?\n");

        Assert.Equal([
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new SubtitleCue(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1.5)),
        ], cues);
    }

    [Fact]
    public void ParsesAssUsingTheFormatLine()
    {
        var cues = SubtitleCues.Parse("""
            [Script Info]
            Title: Teste

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\an8}Olá, tudo bem?\NSim
            Comment: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Não conta
            Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\pos(10,10)}
            Dialogue: 0,1:00:00.25,1:00:01.00,Default,,0,0,0,,Fim
            """);

        Assert.Equal([
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)),
            new SubtitleCue(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(250), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)),
        ], cues);
    }

    [Fact]
    public void ParsesWindows1252AndDropsAbsurdDurations()
    {
        var bytes = Encoding.Latin1.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nAção\n\n2\n00:00:03,000 --> 00:05:00,000\nCréditos\n");

        Assert.Equal([new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))], SubtitleCues.Parse(bytes));
    }
}

public sealed class SubtitleSyncCompareTests
{
    private static readonly List<SubtitleCue> Reference = SyntheticSubtitles.Episode(seed: 1);

    [Fact]
    public void IdenticalTimingIsFullySynced()
    {
        var result = SubtitleSync.Compare(Reference, Reference)!;

        Assert.Equal(SyncVerdict.Synced, result.Verdict);
        Assert.Equal(100, result.Percent);
        Assert.Equal(TimeSpan.Zero, result.Offset);
    }

    [Fact]
    public void TranslationWithTheSameTimingIsSynced()
    {
        var result = SubtitleSync.Compare(SyntheticSubtitles.Translation(Reference, seed: 2), Reference)!;

        Assert.Equal(SyncVerdict.Synced, result.Verdict);
        Assert.True(result.Percent >= 95, $"Precisão {result.Percent}%");
        Assert.True(result.IsGood);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-1.5)]
    [InlineData(45.0)]
    public void FixedOffsetIsDetected(double seconds)
    {
        var candidate = SyntheticSubtitles.Shift(SyntheticSubtitles.Translation(Reference, seed: 3), seconds);

        var result = SubtitleSync.Compare(candidate, Reference)!;

        Assert.Equal(SyncVerdict.Shifted, result.Verdict);
        Assert.InRange(result.Offset.TotalSeconds, -seconds - 0.05, -seconds + 0.05);
        Assert.True(result.Percent < 90, $"Precisão {result.Percent}%");
    }

    [Fact]
    public void HalfSecondOrLessCountsAsSyncedAndShowsTheDifference()
    {
        var result = SubtitleSync.Compare(SyntheticSubtitles.Shift(Reference, 0.4), Reference)!;

        Assert.Equal(SyncVerdict.Synced, result.Verdict);
        Assert.InRange(result.Percent, 70, 90);
        Assert.Equal(-0.4, result.Offset.TotalSeconds, 2);
        Assert.Contains("atrasada", result.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallOffsetStillCountsAsSynced()
    {
        var result = SubtitleSync.Compare(SyntheticSubtitles.Shift(Reference, 0.1), Reference)!;

        Assert.Equal(SyncVerdict.Synced, result.Verdict);
    }

    [Fact]
    public void OtherFrameRateIsDetected()
    {
        var candidate = SyntheticSubtitles.Scale(Reference, 23.976 / 25);

        var result = SubtitleSync.Compare(candidate, Reference)!;

        Assert.Equal(SyncVerdict.FrameRate, result.Verdict);
        Assert.Equal(25 / 23.976, result.Scale, 6);
    }

    [Fact]
    public void AnotherEpisodeDoesNotMatch()
    {
        var result = SubtitleSync.Compare(SyntheticSubtitles.Episode(seed: 99), Reference)!;

        Assert.Equal(SyncVerdict.Mismatch, result.Verdict);
        Assert.False(result.IsGood);
        Assert.Equal("não confere", result.Label);
        Assert.True(result.Confidence < 0.3, $"Concordância {result.Confidence:0.00}");
    }

    [Fact]
    public void TooFewCuesCannotBeCompared()
    {
        Assert.Null(SubtitleSync.Compare(Reference.Take(SubtitleSync.MinCues - 1).ToList(), Reference));
        Assert.Null(SubtitleSync.Compare(Reference, Reference.Take(3).ToList()));
    }

    [Fact]
    public void SortsHashThenSyncedThenShiftedThenMismatch()
    {
        var synced = SubtitleSync.Compare(Reference, Reference)!;
        var shifted = SubtitleSync.Compare(SyntheticSubtitles.Shift(Reference, 3), Reference)!;
        var mismatch = SubtitleSync.Compare(SyntheticSubtitles.Episode(seed: 99), Reference)!;

        Assert.Equal([SyncResult.HashMatch, synced, shifted, mismatch],
            new[] { mismatch, shifted, SyncResult.HashMatch, synced }.OrderByDescending(r => r.SortKey));
    }
}

/// <summary>mkvextract falso: grava a legenda configurada no destino "id:arquivo".</summary>
public sealed class FakeExtractRunner : IMkvMergeRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public string Content { get; set; } = SyntheticSubtitles.ToSrt(SyntheticSubtitles.Episode(seed: 1));
    public int ExitCode { get; set; }

    public Task<MkvMergeResult> RunAsync(string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken ct)
    {
        Calls.Add(arguments);
        if (ExitCode == 2) return Task.FromResult(new MkvMergeResult(2, ["#GUI#error Falhou."]));
        var spec = arguments[^1];
        File.WriteAllText(spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..], Content);
        return Task.FromResult(new MkvMergeResult(0, []));
    }
}

public sealed class SubtitleSyncCheckerTests : IDisposable
{
    private static readonly List<SubtitleCue> ReferenceCues = SyntheticSubtitles.Episode(seed: 1);

    private readonly TempLibrary _library = new();
    private readonly FakeExtractRunner _runner = new();
    private readonly FakeSubtitleProvider _provider = new("Fake");
    private readonly SubtitleProviderSet _providers;
    private readonly SubtitleContentCache _cache;
    private readonly string _toolDir;
    private string? _configuredMkvMerge;

    public SubtitleSyncCheckerTests()
    {
        _toolDir = Path.Combine(_library.Base, "tools");
        Directory.CreateDirectory(_toolDir);
        File.WriteAllBytes(Path.Combine(_toolDir, MkvMergeLocator.ExecutableName), [0]);
        File.WriteAllBytes(Path.Combine(_toolDir, MkvMergeLocator.ExtractExecutableName), [0]);
        _configuredMkvMerge = Path.Combine(_toolDir, MkvMergeLocator.ExecutableName);

        _providers = new SubtitleProviderSet([_provider], TimeProvider.System);
        _cache = new SubtitleContentCache(Path.Combine(_library.Data.CacheDirectory, "subtitle-files"), SubtitleContentCache.DefaultTimeToLive);
        _provider.Content = Encoding.UTF8.GetBytes(SyntheticSubtitles.ToSrt(SyntheticSubtitles.Translation(ReferenceCues, seed: 5)));
    }

    public void Dispose() => _library.Dispose();

    private SubtitleReferenceProvider References() =>
        new(_runner, () => _configuredMkvMerge, Path.Combine(_library.Data.CacheDirectory, "sync-ref"));

    private SubtitleSyncChecker Checker() => new(_providers, References(), _cache);

    private string Video() => _library.File("Show/Show S01E01.mkv", 64);

    private static ExistingSubtitle Embedded(string language = "en", string codec = "S_TEXT/UTF8", int index = 2, bool forced = false) =>
        new(new SubtitleLanguage(language, forced), null, Embedded: true, index, codec);

    private static string ExternalFile(string video, string language = "en")
    {
        var path = Path.ChangeExtension(video, $".{language}.srt");
        File.WriteAllText(path, SyntheticSubtitles.ToSrt(ReferenceCues));
        return path;
    }

    private static VideoSubtitlePlan Plan(string video, IReadOnlyList<ExistingSubtitle> existing, params SubtitleCandidate[] candidates)
    {
        var wanted = new SubtitleLanguage("pt-BR");
        var state = new VideoSubtitleState(video, null, existing, [wanted], false, false);
        var query = new SubtitleQuery(MediaKind.Series, video, 64, null, 1, "tt0000001", 1, 1, Path.GetFileNameWithoutExtension(video), [wanted]);
        return new VideoSubtitlePlan(state, [new SubtitleItem(video, wanted, candidates, SubtitleItemStatus.Found)], query);
    }

    private static SubtitleCandidate Candidate(string id, bool hash = false, string format = "srt") =>
        new("Fake", id, new SubtitleLanguage("pt-BR"), "Show.S01E01.WEB-DL-GRP", hash, true, false, 10, format);

    [Fact]
    public void PrefersAnExternalFileOverAnEmbeddedTrackAndSkipsForced()
    {
        var video = Video();
        var file = ExternalFile(video);
        var external = new ExistingSubtitle(new SubtitleLanguage("en"), file, Embedded: false);
        var forced = new ExistingSubtitle(new SubtitleLanguage("pt-BR", Forced: true), file, Embedded: false);

        var choice = References().Choose(new VideoSubtitleState(video, null, [forced, Embedded(), external], [], false, false));

        Assert.Equal(external, choice.Source);
        Assert.Equal("en (arquivo)", SubtitleReferenceProvider.Describe(choice.Source!));
    }

    [Fact]
    public void ImageTracksCannotBeAReference()
    {
        var choice = References().Choose(new VideoSubtitleState(Video(), null, [Embedded(codec: "S_HDMV/PGS")], [], false, false));

        Assert.False(choice.IsAvailable);
        Assert.Contains("imagens", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedTrackNeedsMkvExtract()
    {
        File.Delete(Path.Combine(_toolDir, MkvMergeLocator.ExtractExecutableName));

        var choice = References().Choose(new VideoSubtitleState(Video(), null, [Embedded()], [], false, false));

        Assert.False(choice.IsAvailable);
        Assert.Contains("MKVToolNix", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsTheEmbeddedTrackOnceAndCachesTheTiming()
    {
        var video = Video();
        var references = References();

        var first = await references.LoadAsync(video, Embedded(index: 3), null, TestContext.Current.CancellationToken);
        var second = await References().LoadAsync(video, Embedded(index: 3), null, TestContext.Current.CancellationToken);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["--gui-mode", video, "tracks"], call.Take(3));
        Assert.StartsWith("3:", call[3], StringComparison.Ordinal);
        Assert.Equal(ReferenceCues.Count, first.Cues.Count);
        Assert.Equal(first.Cues, second.Cues);
        Assert.Equal("en (embutida)", first.Description);
    }

    [Fact]
    public async Task ChecksCandidatesAgainstTheReferenceWithoutDownloadingHashMatches()
    {
        var video = Video();
        var plan = Plan(video, [Embedded()], Candidate("hash", hash: true), Candidate("good"));

        Assert.Equal([("Fake", 1)], Checker().DownloadsNeeded(plan));
        var report = await Checker().CheckAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Null(report.Message);
        Assert.Equal(["good"], _provider.Downloads);
        Assert.Equal(SyncVerdict.Hash, report.Candidates[0].Result!.Verdict);
        Assert.Equal(SyncVerdict.Synced, report.Candidates[1].Result!.Verdict);
        Assert.Empty(Checker().DownloadsNeeded(plan));
    }

    [Fact]
    public async Task ReportsShiftedAndUnreadableCandidates()
    {
        var video = Video();
        ExternalFile(video);
        var existing = new ExistingSubtitle(new SubtitleLanguage("en"), Path.ChangeExtension(video, ".en.srt"), Embedded: false);
        _provider.Content = Encoding.UTF8.GetBytes(SyntheticSubtitles.ToSrt(SyntheticSubtitles.Shift(ReferenceCues, 2.5)));

        var report = await Checker().CheckAsync(Plan(video, [existing], Candidate("shifted"), Candidate("image", format: "sup")),
            null, TestContext.Current.CancellationToken);

        Assert.Empty(_runner.Calls);
        Assert.Equal(SyncVerdict.Shifted, report.Candidates[0].Result!.Verdict);
        Assert.Null(report.Candidates[1].Result);
        Assert.NotNull(report.Candidates[1].Error);
    }

    [Fact]
    public async Task KeepsPartialResultsWhenTheQuotaRunsOut()
    {
        _provider.QuotaAfter = 1;
        var plan = Plan(Video(), [Embedded()], Candidate("a"), Candidate("b"), Candidate("c"));

        var report = await Checker().CheckAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(["a"], _provider.Downloads);
        Assert.NotNull(report.Candidates[0].Result);
        Assert.All(report.Candidates.Skip(1), c => Assert.Contains("Cota", c.Error, StringComparison.Ordinal));
        Assert.NotNull(_providers.DisabledReason(_provider));
    }

    [Fact]
    public async Task WithoutAReferenceNothingIsDownloaded()
    {
        var report = await Checker().CheckAsync(Plan(Video(), [], Candidate("a")), null, TestContext.Current.CancellationToken);

        Assert.Empty(report.Candidates);
        Assert.NotNull(report.Message);
        Assert.Empty(_provider.Downloads);
    }

    [Fact]
    public async Task FailedExtractionIsReported()
    {
        _runner.ExitCode = 2;

        var report = await Checker().CheckAsync(Plan(Video(), [Embedded()], Candidate("a")), null, TestContext.Current.CancellationToken);

        Assert.Contains("mkvextract", report.Message, StringComparison.Ordinal);
        Assert.Empty(_provider.Downloads);
    }

    [Fact]
    public async Task DownloadReusesTheFileFetchedForTheCheck()
    {
        var plan = Plan(Video(), [Embedded()], Candidate("a"));
        await Checker().CheckAsync(plan, null, TestContext.Current.CancellationToken);

        var content = await new SubtitleFetcher(_providers, _cache).FetchAsync(plan.Items[0].Candidates[0], plan.Query!, TestContext.Current.CancellationToken);

        Assert.Single(_provider.Downloads);
        Assert.Equal(SubtitleText.ToUtf8(_provider.Content), content);
    }
}

public sealed class OpenSubtitlesErrorMessageTests
{
    [Theory]
    [InlineData("""{"message":"Invalid file_id"}""", "Invalid file_id")]
    [InlineData("""{"errors":["Error, invalid username/password"],"status":400}""", "Error, invalid username/password")]
    [InlineData("<html>Bad Request</html>", null)]
    [InlineData("", null)]
    public void ExtractsTheApiMessage(string body, string? expected) =>
        Assert.Equal(expected, Subtitles.Providers.OpenSubtitlesProvider.ApiMessage(body));
}
