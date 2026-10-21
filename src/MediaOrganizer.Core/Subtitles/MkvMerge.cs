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

/// <summary>Procura o mkvmerge: caminho configurado, PATH e locais padrão de instalação do MKVToolNix.</summary>
public static class MkvMergeLocator
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "mkvmerge.exe" : "mkvmerge";

    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');
            if (File.Exists(path)) return path;
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, ExecutableName))) return Path.Combine(path, ExecutableName);
            return null;
        }

        return Candidates().FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Candidates()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim('"'), ExecutableName);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (root.Length > 0) yield return Path.Combine(root, "MKVToolNix", ExecutableName);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/mkvmerge";
            yield return "/usr/local/bin/mkvmerge";
            if (Directory.Exists("/Applications"))
            {
                foreach (var app in Directory.EnumerateDirectories("/Applications", "MKVToolNix*.app").OrderDescending(StringComparer.Ordinal))
                    yield return Path.Combine(app, "Contents", "MacOS", "mkvmerge");
            }
        }
        else
        {
            yield return "/usr/bin/mkvmerge";
            yield return "/usr/local/bin/mkvmerge";
            yield return "/snap/bin/mkvmerge";
        }
    }
}
