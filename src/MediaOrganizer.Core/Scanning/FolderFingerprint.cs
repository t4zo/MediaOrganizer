using System.IO.Hashing;
using System.Text;

namespace MediaOrganizer.Core.Scanning;

/// <summary>
/// Hash que identifica o estado de uma pasta já processada. Se nenhum arquivo entrou, saiu ou
/// foi renomeado, o hash é o mesmo e a pasta não precisa ser reprocessada.
/// A data de modificação fica de fora de propósito: a precisão varia entre NTFS/exFAT/APFS/ext4.
/// </summary>
public static class FolderFingerprint
{
    /// <summary>Incrementar sempre que as regras de nomeação mudarem, para forçar reprocessamento.</summary>
    public const int RulesVersion = 3; // v2: episódios especiais (temporada 0); v3: legendas ocultas entram no scan

    public static string Compute(LibraryFolder folder, int tmdbId)
    {
        var baseDir = folder.IsLooseFile ? folder.RootPath : folder.Path;
        var entries = folder.Videos.Concat(folder.Subtitles)
            .Select(file => $"{Path.GetRelativePath(baseDir, file).Replace('\\', '/')}|{SafeLength(file)}")
            .Order(StringComparer.Ordinal);

        var hash = new XxHash128();
        hash.Append(Encoding.UTF8.GetBytes($"v{RulesVersion}|tmdb:{tmdbId}\n"));
        foreach (var entry in entries) hash.Append(Encoding.UTF8.GetBytes(entry + "\n"));
        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }
}
