using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Parsing;
using MediaOrganizer.Core.Planning;

namespace MediaOrganizer.Core.Tests;

public class NamingTests
{
    public static TheoryData<EpisodeCase> Episodes => [.. ConformanceCases.Load().Episodes];
    public static TheoryData<string> NotEpisodes => [.. ConformanceCases.Load().NotEpisodes];
    public static TheoryData<MovieCase> Movies => [.. ConformanceCases.Load().Movies];
    public static TheoryData<SanitizeCase> SanitizeCases => [.. ConformanceCases.Load().Sanitize];
    public static TheoryData<FormatCase> FormatCases => [.. ConformanceCases.Load().Format];
    public static TheoryData<SubtitleLanguageCase> SubtitleLanguages => [.. ConformanceCases.Load().SubtitleLanguages];

    [Theory]
    [MemberData(nameof(Episodes))]
    public void ParsesEpisodeNames(EpisodeCase c)
    {
        var parsed = ReleaseNameParser.ParseEpisode(c.Input);

        Assert.NotNull(parsed);
        Assert.Equal(new ParsedEpisode(c.Show, c.Year, c.Season, c.Episode, c.LastEpisode, c.Title), parsed);
    }

    [Theory]
    [MemberData(nameof(NotEpisodes))]
    public void IgnoresNamesWithoutEpisodeMarker(string input) => Assert.Null(ReleaseNameParser.ParseEpisode(input));

    [Theory]
    [MemberData(nameof(Movies))]
    public void ParsesMovieNames(MovieCase c) =>
        Assert.Equal(new ParsedMovie(c.Title, c.Year), ReleaseNameParser.ParseMovie(c.Input));

    [Theory]
    [MemberData(nameof(SanitizeCases))]
    public void SanitizesTitles(SanitizeCase c) => Assert.Equal(c.Output, FileNameSanitizer.Sanitize(c.Input));

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void FormatsEpisodeNames(FormatCase c) =>
        Assert.Equal(c.Output, NameFormatter.EpisodeBaseName(c.Show, c.Season, c.Episode, c.LastEpisode, c.Title));

    [Theory]
    [MemberData(nameof(SubtitleLanguages))]
    public void DetectsSubtitleLanguage(SubtitleLanguageCase c) =>
        Assert.Equal((c.Language, c.Flag), SubtitleMatcher.DetectLanguage(c.Input));

    [Theory]
    [InlineData("en", "en-US", true)]
    [InlineData("de", "de", true)]
    [InlineData("es", "es", true)]
    [InlineData("ko", "en-US", false)]
    [InlineData("ja", "en-US", false)]
    [InlineData(null, "en-US", false)]
    public void ChoosesTitleLanguage(string? original, string expected, bool usesOriginal)
    {
        Assert.Equal(expected, LanguagePolicy.TitleLanguage(original));
        Assert.Equal(usesOriginal, LanguagePolicy.UsesOriginal(original));
    }

    [Theory]
    [InlineData("Episode 5", true)]
    [InlineData("Folge 12", true)]
    [InlineData("Episódio 3", true)]
    [InlineData("", true)]
    [InlineData("Geheimnisse", false)]
    [InlineData("Episode Five", false)]
    public void DetectsGenericEpisodeTitles(string title, bool generic) =>
        Assert.Equal(generic, LanguagePolicy.IsGenericEpisodeTitle(title));

    [Theory]
    [InlineData("Shōgun", "shogun")]
    [InlineData("Monster: The Ed Gein Story", "monster the ed gein story")]
    [InlineData("Dept. Q", "dept q")]
    [InlineData("Law & Order", "law and order")]
    public void NormalizesTitlesForMatching(string input, string expected) =>
        Assert.Equal(expected, MediaMatcher.Normalize(input));
}
