using MediaOrganizer.Core.Scanning;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

public sealed class SubtitleInventoryTests : IDisposable
{
    private static readonly SubtitleLanguage PtBr = new("pt-BR");
    private static readonly SubtitleLanguage En = new("en");

    private readonly TempLibrary _library = new();

    public void Dispose() => _library.Dispose();

    private string Mkv(string relative, MkvBuilder builder) => builder.WriteTo(Path.Combine(_library.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private LibraryFolder ScanSingle() =>
        Assert.Single(new LibraryScanner([], new JunkDetector([])).Scan(_library.Root, MediaKind.Series));

    private IReadOnlyList<VideoSubtitleState> Inventory(SubtitleLanguageMode mode = SubtitleLanguageMode.All, params SubtitleLanguage[] wanted) =>
        SubtitleInventory.Build(ScanSingle(), wanted.Length == 0 ? [PtBr, En] : wanted, mode, checkEmbedded: true);

    // ---- Leitor de MKV ----

    [Fact]
    public void ReadsSubtitleTracksWithLanguagesAndFlags()
    {
        var path = Mkv("Show/Show S01E01.mkv", new MkvBuilder()
            .Video()
            .Subtitle("por", "pt-BR", "Português")
            .Subtitle("eng", name: "English SDH", hearingImpaired: true)
            .Subtitle("eng", name: "Forced", forced: true));

        var tracks = MatroskaReader.ReadTracks(path);

        Assert.Equal(4, tracks.Count);
        Assert.Equal([0, 1, 2, 3], tracks.Select(t => t.Index));
        var subs = tracks.Where(t => t.IsSubtitle).ToList();
        Assert.Equal(["por", "eng", "eng"], subs.Select(t => t.Language));
        Assert.Equal("pt-BR", subs[0].LanguageBcp47);
        Assert.True(subs[1].HearingImpaired);
        Assert.True(subs[2].Forced);
    }

    [Fact]
    public void FindsTracksAfterClustersThroughSeekHead()
    {
        var path = Mkv("Show/Show S01E01.mkv", new MkvBuilder { TracksAtEnd = true }.Video().Subtitle("eng"));
        Assert.Equal(2, MatroskaReader.ReadTracks(path).Count);

        var noSeek = Mkv("Show/Show S01E02.mkv", new MkvBuilder { TracksAtEnd = true, WithSeekHead = false }.Video().Subtitle("eng"));
        Assert.Empty(MatroskaReader.ReadTracks(noSeek)); // sem SeekHead não lê o arquivo inteiro atrás de Tracks
    }

    [Fact]
    public void TruncatedOrInvalidFilesHaveNoTracks()
    {
        var bytes = new MkvBuilder().Video().Subtitle("eng").Build();
        var truncated = Path.Combine(_library.Root, "t.mkv");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        var notMkv = _library.File("x.mkv", 2000);

        Assert.Empty(MatroskaReader.ReadTracks(truncated));
        Assert.Empty(MatroskaReader.ReadTracks(notMkv));
        Assert.Empty(MatroskaReader.ReadTracks(Path.Combine(_library.Root, "missing.mkv")));
    }

    // ---- Idiomas ----

    [Theory]
    [InlineData("por", "pt-BR", null, "pt-BR")]
    [InlineData("por", null, "Portuguese (Brazil)", "pt-BR")]
    [InlineData("por", null, "Português (Brasil)", "pt-BR")]
    [InlineData("pob", null, null, "pt-BR")]
    [InlineData("por", null, null, "pt")]
    [InlineData("eng", null, null, "en")]
    [InlineData("eng", "en-US", null, "en")]
    [InlineData("eng", "en-GB", null, "en")]
    [InlineData("und", null, "English", "en")]
    [InlineData("spa", null, null, "es")]
    public void MapsTrackLanguages(string language, string? bcp47, string? name, string expected)
    {
        var track = new MatroskaTrack(0, 1, MatroskaTrack.SubtitleType, "S_TEXT/UTF8", language, bcp47, name, false, false, false);
        Assert.Equal(expected, SubtitleInventory.FromTrack(track)?.Code);
    }

    [Fact]
    public void UndefinedTrackWithoutNameHasNoLanguage()
    {
        var track = new MatroskaTrack(0, 1, MatroskaTrack.SubtitleType, null, "und", null, null, false, false, false);
        Assert.Null(SubtitleInventory.FromTrack(track));
    }

    // ---- Inventário ----

    [Fact]
    public void ExternalAndEmbeddedSubtitlesSatisfyLanguages()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.pt-BR.srt");
        Mkv("Show/Show S01E02.mkv", new MkvBuilder().Video().Subtitle("por", "pt-BR").Subtitle("eng"));
        Mkv("Show/Show S01E03.mkv", new MkvBuilder().Video().Subtitle("eng"));
        _library.File("Show/Show S01E04.mkv");
        _library.File("Show/Subs/Show S01E04/3_English.srt");

        var states = Inventory().ToDictionary(s => Path.GetFileName(s.VideoPath));

        Assert.Equal([En], states["Show S01E01.mkv"].Missing);
        Assert.Empty(states["Show S01E02.mkv"].Missing);
        Assert.Equal([PtBr], states["Show S01E03.mkv"].Missing);
        Assert.Equal([PtBr], states["Show S01E04.mkv"].Missing);
        Assert.Equal((1, 3), (states["Show S01E03.mkv"].Episode!.Season, states["Show S01E03.mkv"].Episode!.Episode));
    }

    [Fact]
    public void PortugueseWithoutRegionStillMissesPtBrButRaisesDoubt()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.pt.srt");

        var state = Assert.Single(Inventory(SubtitleLanguageMode.All, PtBr));

        Assert.Equal([PtBr], state.Missing);
        Assert.NotNull(state.DoubtFor(PtBr));
        Assert.Null(state.DoubtFor(En));
    }

    [Fact]
    public void SubtitleWithoutLanguageRaisesDoubt()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.srt");

        var state = Assert.Single(Inventory());

        Assert.Equal([PtBr, En], state.Missing);
        Assert.NotNull(state.DoubtFor(En));
    }

    [Fact]
    public void ForcedDoesNotSatisfyNormalAndViceVersa()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.pt-BR.forced.srt");
        Mkv("Show/Show S01E02.mkv", new MkvBuilder().Video().Subtitle("eng", forced: true));

        var states = Inventory(SubtitleLanguageMode.All, PtBr, En, new SubtitleLanguage("pt-BR", Forced: true)).ToDictionary(s => Path.GetFileName(s.VideoPath));

        Assert.Equal([PtBr, En], states["Show S01E01.mkv"].Missing);
        Assert.Equal([PtBr, En, new SubtitleLanguage("pt-BR", Forced: true)], states["Show S01E02.mkv"].Missing);
    }

    [Fact]
    public void SdhSatisfiesTheNormalSubtitle()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.en.sdh.srt");

        Assert.Equal([PtBr], Assert.Single(Inventory()).Missing);
    }

    [Fact]
    public void FirstAvailableOnlyMissesLanguagesBeforeTheBestPresent()
    {
        _library.File("Show/Show S01E01.mkv");
        _library.File("Show/Show S01E01.pt-BR.srt");
        _library.File("Show/Show S01E02.mkv");
        _library.File("Show/Show S01E02.en.srt");
        _library.File("Show/Show S01E03.mkv");

        var states = Inventory(SubtitleLanguageMode.FirstAvailable).ToDictionary(s => Path.GetFileName(s.VideoPath));

        Assert.Empty(states["Show S01E01.mkv"].Missing);
        Assert.Equal([PtBr], states["Show S01E02.mkv"].Missing);
        Assert.Equal([PtBr, En], states["Show S01E03.mkv"].Missing);
    }

    // ---- Scanner com legendas ocultas ----

    [Fact]
    public void ScannerListsHiddenSubtitlesButNotOtherHiddenFiles()
    {
        _library.File("Show/Show S01E01.mkv");
        var hiddenSub = _library.File("Show/Show S01E01.pt-BR.srt");
        var hiddenVideo = _library.File("Show/Show S01E02.mkv");
        _library.File("Show/._Show S01E01.pt-BR.srt"); // AppleDouble do macOS
        _library.File("Show/.mediaorganizer/originals/Show S01E03.mkv");
        _library.File("Show/.mediaorganizer/tmp/x.srt");
        SetHidden(hiddenSub);
        SetHidden(hiddenVideo);

        var folder = ScanSingle();

        Assert.Equal(["Show S01E01.pt-BR.srt"], folder.Subtitles.Select(Path.GetFileName));
        Assert.Equal(OperatingSystem.IsWindows() ? ["Show S01E01.mkv"] : ["Show S01E01.mkv", "Show S01E02.mkv"],
            folder.Videos.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task HiddenSubtitleIsRenamedWithItsVideoAndStaysHidden()
    {
        var tmdb = new FakeTmdb()
            .Search("Show", "en-US", (1, "Show", "Show", "en"))
            .Show(1, "Show", "Show", "en")
            .Season(1, 1, "en-US", (1, "Pilot"));
        var harness = new Harness(_library, tmdb);
        _library.File("Show/show.s01e01.720p.mkv");
        SetHidden(_library.File("Show/show.s01e01.720p.pt-BR.srt"));

        var plan = Assert.Single(await harness.AnalyzeAsync());
        harness.Apply(plan);

        var renamed = Path.Combine(_library.Root, "Show", "Show S01E01 - Pilot.pt-BR.srt");
        Assert.True(File.Exists(renamed));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(renamed).HasFlag(FileAttributes.Hidden));
    }

    private static void SetHidden(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }
}
