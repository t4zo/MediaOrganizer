namespace MediaOrganizer.Core.Subtitles;

/// <summary>Grupo, fonte e resolução de um nome de release ("Show.S01E01.1080p.WEB-DL.x264-NTb").</summary>
public sealed partial record ReleaseInfo(string? Group, string? Source, string? Resolution)
{
    public static ReleaseInfo Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new ReleaseInfo(null, null, null);
        var text = MediaFileTypes.IsVideo(name) || MediaFileTypes.IsSubtitle(name) ? Path.GetFileNameWithoutExtension(name) : name;

        var group = GroupRegex().Match(text) is { Success: true } g ? g.Groups[1].Value : null;
        var resolution = ResolutionRegex().Match(text) is { Success: true } r ? r.Groups[1].Value + "p" : null;
        var source = SourceRegex().Match(text) is { Success: true } s ? NormalizeSource(s.Value) : null;
        return new ReleaseInfo(group, source, resolution);
    }

    private static string NormalizeSource(string value) =>
        value.Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "WEBDL" => "WEB-DL",
            "WEBRIP" => "WEBRip",
            "WEB" => "WEB",
            "BLURAY" or "BDRIP" or "BRRIP" or "BDREMUX" => "BluRay",
            "HDTV" => "HDTV",
            "DVDRIP" or "DVD" => "DVD",
            var other => other,
        };

    [GeneratedRegex(@"-([A-Za-z0-9]+)(?:\[[^\]]*\])?$")]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"\b(480|576|720|1080|2160)[pi]\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"\b(web[-.]?dl|web[-.]?rip|bluray|blu[-.]ray|bdrip|brrip|bdremux|hdtv|dvdrip|dvd|web)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();
}

/// <summary>Pontua candidatos para escolher a legenda com mais chance de estar sincronizada.</summary>
public static class SubtitleScorer
{
    public const int HashPoints = 100;
    public const int IdPoints = 40;
    public const int GroupPoints = 20;
    public const int SourcePoints = 10;
    public const int ResolutionPoints = 5;
    public const int HearingImpairedMatchPoints = 5;
    public const int HearingImpairedAvoidPenalty = -30;
    public const int SeasonPackPenalty = -10;

    public static int Score(SubtitleCandidate candidate, ReleaseInfo video, HearingImpairedPreference preference)
    {
        var score = 0;
        if (candidate.HashMatch) score += HashPoints;
        if (candidate.IdMatch) score += IdPoints;

        var release = ReleaseInfo.Parse(candidate.ReleaseName);
        if (Same(video.Group, release.Group)) score += GroupPoints;
        if (Same(video.Source, release.Source)) score += SourcePoints;
        if (Same(video.Resolution, release.Resolution)) score += ResolutionPoints;

        var hi = candidate.Language.HearingImpaired;
        score += preference switch
        {
            HearingImpairedPreference.Avoid => hi ? HearingImpairedAvoidPenalty : HearingImpairedMatchPoints,
            HearingImpairedPreference.Prefer => hi ? HearingImpairedMatchPoints : 0,
            _ => 0,
        };

        if (candidate.IsSeasonPack) score += SeasonPackPenalty;
        return score;
    }

    /// <summary>Pontua e ordena: maior pontuação, depois mais downloads.</summary>
    public static IReadOnlyList<SubtitleCandidate> Rank(IEnumerable<SubtitleCandidate> candidates, string releaseName, HearingImpairedPreference preference)
    {
        var video = ReleaseInfo.Parse(releaseName);
        return [.. candidates
            .Select(c => c with { Score = Score(c, video, preference) })
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Downloads)];
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
