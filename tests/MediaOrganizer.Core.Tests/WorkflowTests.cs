using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly TempLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();

    public void Dispose() => _library.Dispose();

    private FakeTmdb DarkShow() => _tmdb
        .Search("Dark", "en-US", (70523, "Dark", "Dark", "de"))
        .Show(70523, "Dark", "Dark", "de")
        .Season(70523, 1, "de", (1, "Geheimnisse"), (2, "Lügen"), (3, "Folge 3"))
        .Season(70523, 1, "en-US", (1, "Secrets"), (2, "Lies"), (3, "Past and Present"));

    [Fact]
    public async Task RenamesEpisodesWithOriginalLanguageTitlesAndSubtitles()
    {
        DarkShow();
        _library.File("Dark/Dark.S01E01.1080p.WEB.x265-GRP.mkv");
        _library.File("Dark/Dark.S01E01.1080p.WEB.x265-GRP.en.srt");
        _library.File("Dark/Dark S01E02 - Lies.mkv");
        _library.File("Dark/Dark S01E03.mp4");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync());
        Assert.Equal(FolderStatus.Changes, plan.Status);
        Assert.False(plan.LowConfidence);

        harness.Apply(plan);

        Assert.Equal(
        [
            "Dark/Dark S01E01 - Geheimnisse.en.srt",
            "Dark/Dark S01E01 - Geheimnisse.mkv",
            "Dark/Dark S01E02 - Lügen.mkv",
            "Dark/Dark S01E03 - Past and Present.mp4", // "Folge 3" é genérico → inglês
        ], _library.Files());
    }

    [Fact]
    public async Task UsesEnglishForNonLatinShowsFoundByLocalizedName()
    {
        _tmdb
            .Search("Round 6", "pt-BR", (93405, "Round 6", "오징어 게임", "ko"))
            .Show(93405, "Squid Game", "오징어 게임", "ko", "2021-09-17")
            .Season(93405, 2, "en-US", (1, "Bread and Lottery"));
        _library.File("Round 6/Round 6 S02E01.mkv");
        var harness = new Harness(_library, _tmdb);

        harness.Apply(Assert.Single(await harness.AnalyzeAsync()));

        Assert.Equal(["Round 6/Squid Game S02E01 - Bread and Lottery.mkv"], _library.Files());
    }

    [Fact]
    public async Task KeepsFilesInReleaseSubfoldersAndRenamesRarbgSubtitles()
    {
        _tmdb
            .Search("Better Call Saul", "en-US", (60059, "Better Call Saul", "Better Call Saul", "en"))
            .Show(60059, "Better Call Saul", "Better Call Saul", "en", "2015-02-08")
            .Season(60059, 1, "en-US", (1, "Uno"));
        const string release = "Better Call Saul/Better.Call.Saul.S01.1080p.BluRay.x265-RARBG";
        _library.File($"{release}/Better.Call.Saul.S01E01.1080p.BluRay.x265-RARBG.mp4");
        _library.File($"{release}/Subs/Better.Call.Saul.S01E01.1080p.BluRay.x265-RARBG/3_English.srt");
        _library.File($"{release}/RARBG.txt");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync());
        Assert.Single(plan.Folder.Junk);
        harness.Apply(plan);

        Assert.Equal(
        [
            $"{release}/Better Call Saul S01E01 - Uno.mp4",
            $"{release}/RARBG.txt",
            $"{release}/Subs/Better.Call.Saul.S01E01.1080p.BluRay.x265-RARBG/Better Call Saul S01E01 - Uno.en.srt",
        ], _library.Files());
    }

    [Fact]
    public async Task SkipsFolderWhenNothingChangedSinceLastRun()
    {
        DarkShow();
        _library.File("Dark/Dark.S01E01.WEB.mkv");
        var harness = new Harness(_library, _tmdb);

        harness.Apply(Assert.Single(await harness.AnalyzeAsync()));

        var second = Assert.Single(await harness.AnalyzeAsync());
        Assert.Equal(FolderStatus.AlreadyProcessed, second.Status);

        _library.File("Dark/Dark.S01E02.WEB.mkv");
        var third = Assert.Single(await harness.AnalyzeAsync());
        Assert.Equal(FolderStatus.Changes, third.Status);
        Assert.Equal(1, third.ChangeCount);

        var forced = Assert.Single(await harness.AnalyzeAsync(force: true));
        Assert.NotEqual(FolderStatus.AlreadyProcessed, forced.Status);
    }

    [Fact]
    public async Task FolderAlreadyInStandardIsRememberedAsProcessed()
    {
        DarkShow();
        _library.File("Dark/Dark S01E01 - Geheimnisse.mkv");
        var harness = new Harness(_library, _tmdb);

        Assert.Equal(FolderStatus.NoChanges, Assert.Single(await harness.AnalyzeAsync()).Status);
        Assert.Equal(FolderStatus.AlreadyProcessed, Assert.Single(await harness.AnalyzeAsync()).Status);
    }

    [Fact]
    public async Task UndoRestoresOriginalNames()
    {
        DarkShow();
        _library.File("Dark/Dark.S01E01.WEB.mkv");
        _library.File("Dark/Dark.S01E02.WEB.mkv");
        var harness = new Harness(_library, _tmdb);
        var before = _library.Files();

        var result = harness.Apply(Assert.Single(await harness.AnalyzeAsync()));
        Assert.NotEqual(before, _library.Files());

        var undo = harness.Executor.Undo(result.Journal);

        Assert.Equal(2, undo.Succeeded);
        Assert.Equal(before, _library.Files());
        Assert.False(Assert.Single(harness.History.List()).CanUndo);
    }

    [Fact]
    public async Task FlagsConflictsInsteadOfOverwriting()
    {
        DarkShow();
        _library.File("Dark/Dark.S01E01.720p.mkv");
        _library.File("Dark/Dark.S01E01.1080p.mkv");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync());

        Assert.Equal(FolderStatus.NeedsReview, plan.Status);
        Assert.All(plan.Items, i => Assert.Equal(ItemStatus.Conflict, i.Status));
    }

    [Fact]
    public async Task MarksUnidentifiedFilesAsSkipped()
    {
        DarkShow();
        _library.File("Dark/Dark.S01E01.mkv");
        _library.File("Dark/Making of.mkv");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync());

        Assert.Contains(plan.Items, i => i.Status == ItemStatus.Skipped && i.SourcePath.EndsWith("Making of.mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MovesLooseMovieIntoOwnFolderAndUndoRemovesIt()
    {
        _tmdb
            .Route("search/movie?query=Cidade%20de%20Deus&", """{"results":[{"id":598,"title":"City of God","original_title":"Cidade de Deus","original_language":"pt","release_date":"2002-08-30","popularity":30}]}""")
            .Route("movie/598?language=en-US", """{"id":598,"title":"City of God","original_title":"Cidade de Deus","original_language":"pt","release_date":"2002-08-30"}""");
        _library.File("Cidade.de.Deus.2002.720p.BRRip.mkv");
        _library.File("Cidade.de.Deus.2002.720p.BRRip.pt-BR.srt");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync(MediaKind.Movie));
        var result = harness.Apply(plan);

        Assert.Equal(["Cidade de Deus (2002)/Cidade de Deus (2002).mkv", "Cidade de Deus (2002)/Cidade de Deus (2002).pt-BR.srt"], _library.Files());

        harness.Executor.Undo(result.Journal);
        Assert.Equal(["Cidade.de.Deus.2002.720p.BRRip.mkv", "Cidade.de.Deus.2002.720p.BRRip.pt-BR.srt"], _library.Files());
        Assert.False(Directory.Exists(Path.Combine(_library.Root, "Cidade de Deus (2002)")));
    }

    [Fact]
    public async Task RenamesMovieFolderAfterItsFiles()
    {
        _tmdb
            .Route("search/movie?query=Inception&", """{"results":[{"id":27205,"title":"Inception","original_title":"Inception","original_language":"en","release_date":"2010-07-15","popularity":80}]}""")
            .Route("movie/27205?language=en-US", """{"id":27205,"title":"Inception","original_title":"Inception","original_language":"en","release_date":"2010-07-15"}""");
        _library.File("Inception.2010.1080p.BluRay.x264-GRP/Inception.2010.1080p.BluRay.x264-GRP.mkv", size: 10);
        _library.File("Inception.2010.1080p.BluRay.x264-GRP/sample.mkv");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync(MediaKind.Movie));
        harness.Apply(plan);

        Assert.Equal(["Inception (2010)/Inception (2010).mkv", "Inception (2010)/sample.mkv"], _library.Files());
        Assert.Equal(FolderStatus.AlreadyProcessed, Assert.Single(await harness.AnalyzeAsync(MediaKind.Movie)).Status);
    }

    [Fact]
    public void ScannerIgnoresExtrasAndFindsJunkAndEmptyFolders()
    {
        _library.File("Breaking Bad/Breaking Bad S01E01 - Pilot.mkv");
        _library.File("Breaking Bad/Extras/S01/Breaking Bad - S01 Extra - Inside Breaking Bad.m4v");
        _library.File("Breaking Bad/PSArips.com.txt");
        _library.Dir("Breaking Bad/Empty/Nested");
        var harness = new Harness(_library, _tmdb);

        var folder = Assert.Single(harness.Analyzer.Scan(_library.Root, MediaKind.Series));

        Assert.Single(folder.Videos);
        Assert.EndsWith("PSArips.com.txt", Assert.Single(folder.Junk), StringComparison.Ordinal);
        Assert.Equal(2, folder.EmptyDirectories.Count);
    }

    [Fact]
    public void FingerprintChangesWhenFilesChange()
    {
        _library.File("Dark/a.mkv");
        var scanner = new LibraryScanner([], new JunkDetector([]));
        var dir = Path.Combine(_library.Root, "Dark");

        var first = FolderFingerprint.Compute(scanner.ScanFolder(MediaKind.Series, _library.Root, dir), 1);
        Assert.Equal(first, FolderFingerprint.Compute(scanner.ScanFolder(MediaKind.Series, _library.Root, dir), 1));
        Assert.NotEqual(first, FolderFingerprint.Compute(scanner.ScanFolder(MediaKind.Series, _library.Root, dir), 2));

        File.Move(Path.Combine(dir, "a.mkv"), Path.Combine(dir, "b.mkv"));
        Assert.NotEqual(first, FolderFingerprint.Compute(scanner.ScanFolder(MediaKind.Series, _library.Root, dir), 1));
    }

    [Fact]
    public void CaseOnlyRenameWorks()
    {
        var source = _library.File("Dark/dark s01e01.mkv");
        var target = Path.Combine(Path.GetDirectoryName(source)!, "Dark S01E01.mkv");
        var item = new RenameItem(ItemKind.Video, source, target, ItemStatus.Ok);

        Assert.Equal(ItemStatus.Ok, Assert.Single(ConflictDetector.Apply([item])).Status);

        new Execution.RenameExecutor(new Execution.HistoryStore(_library.Data)).Apply(_library.Root, MediaKind.Series, "case", [item], []);
        Assert.Equal(["Dark/Dark S01E01.mkv"], _library.Files());
    }
}
