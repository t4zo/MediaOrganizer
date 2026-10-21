using System.Text.Json;

namespace MediaOrganizer.Core.Tests;

public sealed record EpisodeCase(string Input, string Show, int? Year, int Season, int Episode, int? LastEpisode, string? Title)
{
    public override string ToString() => Input;
}

public sealed record MovieCase(string Input, string Title, int? Year)
{
    public override string ToString() => Input;
}

public sealed record SanitizeCase(string Input, string Output)
{
    public override string ToString() => Input;
}

public sealed record FormatCase(string Show, int Season, int Episode, int? LastEpisode, string? Title, string Output)
{
    public override string ToString() => Output;
}

public sealed record SubtitleLanguageCase(string Input, string? Language, string? Flag)
{
    public override string ToString() => $"'{Input}'";
}

public sealed record SpecialTitleCase(string Input, IReadOnlyList<string> Shows, string? Output)
{
    public override string ToString() => Input;
}

/// <summary>Casos de docs/test-cases.json, compartilhados com futuros ports.</summary>
public sealed record ConformanceCases(
    IReadOnlyList<EpisodeCase> Episodes,
    IReadOnlyList<string> NotEpisodes,
    IReadOnlyList<MovieCase> Movies,
    IReadOnlyList<SanitizeCase> Sanitize,
    IReadOnlyList<FormatCase> Format,
    IReadOnlyList<SubtitleLanguageCase> SubtitleLanguages,
    IReadOnlyList<SpecialTitleCase> SpecialTitles)
{
    private static readonly Lazy<ConformanceCases> Instance = new(() =>
        JsonSerializer.Deserialize<ConformanceCases>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-cases.json")),
            JsonSerializerOptions.Web)!);

    public static ConformanceCases Load() => Instance.Value;
}
