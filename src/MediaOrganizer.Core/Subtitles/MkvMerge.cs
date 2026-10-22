using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MediaOrganizer.Core.Subtitles;

public sealed record MkvMergeResult(int ExitCode, IReadOnlyList<string> Output)
{
    /// <summary>0 = ok; 1 = ok com avisos; 2 = erro.</summary>
    public bool Succeeded => ExitCode is 0 or 1;

    /// <summary>Mensagens de erro do modo --gui-mode ("#GUI#error ..."), ou as últimas linhas.</summary>
    public string ErrorText
    {
        get
        {
            var errors = Output.Where(l => l.StartsWith("#GUI#error", StringComparison.Ordinal)).Select(l => l["#GUI#error".Length..].Trim()).ToList();
            return string.Join(" ", errors.Count > 0 ? errors : Output.TakeLast(3));
        }
    }
}

/// <summary>Executa o mkvmerge (os testes usam uma versão falsa).</summary>
public interface IMkvMergeRunner
{
    Task<MkvMergeResult> RunAsync(string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Processo real do mkvmerge, com argumentos em lista (sem shell) e progresso do --gui-mode.</summary>
public sealed class MkvMergeRunner : IMkvMergeRunner
{
    private const string ProgressPrefix = "#GUI#progress ";

    public async Task<MkvMergeResult> RunAsync(string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o mkvmerge.");
        var output = new List<string>();
        try
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                output.Add(line);
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal) &&
                    int.TryParse(line[ProgressPrefix.Length..].TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                    progress?.Report(percent / 100.0);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            output.AddRange((await errors.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        return new MkvMergeResult(process.ExitCode, output);
    }
}

/// <summary>Procura as ferramentas do MKVToolNix: caminho configurado, PATH e locais padrão de instalação.</summary>
public static class MkvMergeLocator
{
    public static string ExecutableName => Executable("mkvmerge");

    public static string ExtractExecutableName => Executable("mkvextract");

    private static string Executable(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');
            if (File.Exists(path)) return path;
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, ExecutableName))) return Path.Combine(path, ExecutableName);
            return null;
        }

        return Candidates("mkvmerge").FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// O mkvextract vem junto com o mkvmerge: com caminho configurado, só na pasta dele; sem, na pasta do mkvmerge
    /// encontrado e depois nos mesmos locais de busca.
    /// </summary>
    public static string? FindExtract(string? configuredMkvMerge)
    {
        var merge = Find(configuredMkvMerge);
        if (merge is not null && Path.Combine(Path.GetDirectoryName(merge)!, ExtractExecutableName) is var sibling && File.Exists(sibling))
            return sibling;
        return string.IsNullOrWhiteSpace(configuredMkvMerge) ? Candidates("mkvextract").FirstOrDefault(File.Exists) : null;
    }

    private static IEnumerable<string> Candidates(string tool)
    {
        var name = Executable(tool);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim('"'), name);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (root.Length > 0) yield return Path.Combine(root, "MKVToolNix", name);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/" + tool;
            yield return "/usr/local/bin/" + tool;
            if (Directory.Exists("/Applications"))
            {
                foreach (var app in Directory.EnumerateDirectories("/Applications", "MKVToolNix*.app").OrderDescending(StringComparer.Ordinal))
                    yield return Path.Combine(app, "Contents", "MacOS", tool);
            }
        }
        else
        {
            yield return "/usr/bin/" + tool;
            yield return "/usr/local/bin/" + tool;
            yield return "/snap/bin/" + tool;
        }
    }
}
