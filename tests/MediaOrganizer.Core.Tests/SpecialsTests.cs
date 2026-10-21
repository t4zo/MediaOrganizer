using MediaOrganizer.Core.Planning;

namespace MediaOrganizer.Core.Tests;

public sealed class SpecialsTests : IDisposable
{
    private readonly TempLibrary _library = new();
    private readonly FakeTmdb _tmdb = new();

    public SpecialsTests() => _tmdb
        .Search("Breaking Bad", "en-US", (1396, "Breaking Bad", "Breaking Bad", "en"))
        .Show(1396, "Breaking Bad", "Breaking Bad", "en", "2008-01-20")
        .Season(1396, 1, "en-US", (1, "Pilot"))
        .Season(1396, 0, "en-US", (1, "Good Cop Bad Cop"), (2, "Pilot Extended Version"), (3, "Episode 3"));

    public void Dispose() => _library.Dispose();

    [Fact]
    public async Task RenamesNumberedSpecialsWithSeasonZeroTitles()
    {
        _library.File("Breaking Bad/Breaking.Bad.S01E01.720p.mkv");
        _library.File("Breaking Bad/Specials/Breaking.Bad.S00E01.720p.WEB-DL.mkv");
        var harness = new Harness(_library, _tmdb);

        harness.Apply(Assert.Single(await harness.AnalyzeAsync()));

        Assert.Equal(
        [
            "Breaking Bad/Breaking Bad S01E01 - Pilot.mkv",
            "Breaking Bad/Specials/Breaking Bad S00E01 - Good Cop Bad Cop.mkv",
        ], _library.Files());
    }

    [Fact]
    public async Task SuggestsUnnumberedSpecialByTitleButRequiresConfirmation()
    {
        var file = _library.File("Breaking Bad/Specials/Breaking Bad - Pilot Extended.mkv");
        var harness = new Harness(_library, _tmdb);

        var plan = Assert.Single(await harness.AnalyzeAsync());
        var item = Assert.Single(plan.Items);

        Assert.Equal(file, item.SourcePath);
        Assert.Equal("Breaking Bad S00E02 - Pilot Extended Version.mkv", Path.GetFileName(item.TargetPath));
        Assert.Equal(ItemStatus.Warning, item.Status);
        Assert.True(item.NeedsConfirmation);
        Assert.False(item.WithTarget(item.TargetPath + "x").NeedsConfirmation); // editar o nome confirma
    }

    [Fact]
    public async Task UnnumberedSpecialWithoutMatchingTitleIsSkipped()
    {
        _library.File("Breaking Bad/Specials/Making of Season 5.mkv");
        var harness = new Harness(_library, _tmdb);

        var item = Assert.Single(Assert.Single(await harness.AnalyzeAsync()).Items);

        Assert.Equal(ItemStatus.Skipped, item.Status);
    }

    [Fact]
    public async Task UnnumberedFileOutsideSpecialsFolderIsNotMatchedByTitle()
    {
        _library.File("Breaking Bad/Breaking Bad - Pilot Extended.mkv");
        var harness = new Harness(_library, _tmdb);

        var item = Assert.Single(Assert.Single(await harness.AnalyzeAsync()).Items);

        Assert.Equal(ItemStatus.Skipped, item.Status);
    }

    [Fact]
    public void SpecialsFolderIsIgnoredWhenOptionIsOff()
    {
        _library.File("Breaking Bad/Breaking.Bad.S01E01.mkv");
        _library.File("Breaking Bad/Specials/Breaking.Bad.S00E01.mkv");
        var harness = new Harness(_library, _tmdb);
        harness.Settings.Update(s => s.IncludeSpecials = false);

        var folder = Assert.Single(harness.Analyzer.Scan(_library.Root, MediaKind.Series));

        Assert.Single(folder.Videos);
        Assert.Empty(folder.SpecialVideos);
    }

    [Fact]
    public void OptionOverridesSpecialsInLegacyIgnoredFolders()
    {
        _library.File("Breaking Bad/Season 00/Breaking.Bad.S00E01.mkv");
        _library.File("Breaking Bad/Specials/Breaking Bad - Pilot Extended.mkv");
        var harness = new Harness(_library, _tmdb);
        harness.Settings.Update(s => s.IgnoredFolders.Add("Specials")); // configuração salva pela v1.0

        var folder = Assert.Single(harness.Analyzer.Scan(_library.Root, MediaKind.Series));

        Assert.Equal(2, folder.Videos.Count);
        Assert.Equal(2, folder.SpecialVideos.Count);
    }
}
