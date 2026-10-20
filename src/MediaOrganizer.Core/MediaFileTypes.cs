namespace MediaOrganizer.Core;

/// <summary>Extensões reconhecidas como vídeo ou legenda.</summary>
public static class MediaFileTypes
{
    public static readonly FrozenSet<string> Video = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg", ".flv");

    public static readonly FrozenSet<string> Subtitle = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup");

    public static bool IsVideo(string path) => Video.Contains(Path.GetExtension(path));

    public static bool IsSubtitle(string path) => Subtitle.Contains(Path.GetExtension(path));
}
