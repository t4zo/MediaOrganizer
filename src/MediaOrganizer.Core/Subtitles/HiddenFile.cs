namespace MediaOrganizer.Core.Subtitles;

/// <summary>
/// Atributo de arquivo oculto sem mudar o nome (um ponto no início faria os players deixarem de associar a legenda).
/// Windows: atributo Hidden. macOS: flag UF_HIDDEN (o .NET grava via chflags). Linux: não existe sem renomear.
/// </summary>
public static class HiddenFile
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <returns><c>false</c> se o sistema ou o sistema de arquivos não suportar (a legenda fica visível).</returns>
    public static bool TryHide(string path)
    {
        if (!IsSupported) return false;
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
