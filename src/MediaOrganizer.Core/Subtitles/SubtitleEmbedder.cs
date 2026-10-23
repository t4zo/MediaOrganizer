using System.Globalization;
using MediaOrganizer.Core.Execution;
using MediaOrganizer.Core.Scanning;

namespace MediaOrganizer.Core.Subtitles;

/// <summary>Uma legenda (arquivo de texto em UTF-8) para virar faixa do MKV.</summary>
public sealed record EmbedTrack(string SubtitlePath, SubtitleLanguage Language);

/// <param name="BackupPath">Vídeo original guardado em ".mediaorganizer/originals" (ou <c>null</c> se foi para a Lixeira).</param>
/// <param name="AddedTrackIds">IDs do mkvmerge das faixas novas (para desfazer sem backup).</param>
public sealed record EmbedResult(string? BackupPath, List<int> AddedTrackIds);

/// <summary>
/// Embute legendas no MKV com o mkvmerge, sem nunca escrever sobre o original: gera o vídeo novo num temporário
/// no mesmo disco, confere se todas as faixas originais continuam lá e só então troca os arquivos, guardando o
/// original como backup (padrão) para desfazer.
/// </summary>
public sealed class SubtitleEmbedder(IMkvMergeRunner runner, Func<string?> configuredPath, Action<string>? trash = null,
    Func<string, long?>? freeSpace = null) : IEmbedUndo
{
    private const double SpaceMargin = 1.05;

    private readonly Action<string> _trash = trash ?? RecycleBin.Send;
    private readonly Func<string, long?> _freeSpace = freeSpace ?? FreeSpace;

    public string? ToolPath => MkvMergeLocator.Find(configuredPath());

    public bool IsAvailable => ToolPath is not null;

    /// <summary>"mkvmerge v80.0 ('...')" ou a mensagem de erro (para o botão "Testar").</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        if (ToolPath is not { } tool) return "mkvmerge não encontrado. Instale o MKVToolNix ou informe o caminho.";
        try
        {
            var result = await runner.RunAsync(tool, ["--version"], null, ct).ConfigureAwait(false);
            return result.Output.FirstOrDefault(l => l.StartsWith("mkvmerge", StringComparison.OrdinalIgnoreCase)) ?? result.ErrorText;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Não foi possível executar o mkvmerge: {e.Message}";
        }
    }

    public static string WorkDirectory(string videoPath) => Path.Combine(Path.GetDirectoryName(videoPath)!, LibraryScanner.WorkFolderName);

    public async Task<EmbedResult> EmbedAsync(string videoPath, IReadOnlyList<EmbedTrack> tracks, bool keepOriginal,
        IProgress<double>? progress, CancellationToken ct)
    {
        var tool = ToolPath ?? throw new IOException("mkvmerge não encontrado. Instale o MKVToolNix ou informe o caminho nas Configurações.");
        if (!MatroskaReader.IsMatroska(videoPath)) throw new IOException("Embutir só é possível em MKV.");

        var original = MatroskaReader.ReadTracks(videoPath);
        if (original.Count == 0) throw new IOException("Não foi possível ler as faixas do MKV.");

        var size = new FileInfo(videoPath).Length;
        if (_freeSpace(videoPath) is { } free && free < size * SpaceMargin)
            throw new IOException($"Espaço insuficiente no disco: são necessários {Gigabytes(size * SpaceMargin)} livres para gerar o vídeo novo.");

        var temp = TempPath(videoPath);
        List<string> arguments = ["--gui-mode", "-o", temp, videoPath];
        foreach (var track in tracks) arguments.AddRange(TrackArguments(track));

        var result = await RunAsync(tool, arguments, temp, progress, ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException($"O mkvmerge falhou: {result.ErrorText}");

        var updated = MatroskaReader.ReadTracks(temp);
        if (!Preserves(original, updated, tracks.Count) || new FileInfo(temp).Length < size)
        {
            File.Delete(temp);
            throw new IOException("O vídeo gerado não tem todas as faixas do original; nada foi alterado.");
        }

        var backup = keepOriginal ? BackupPath(videoPath) : null;
        Replace(videoPath, temp, backup);
        return new EmbedResult(backup, [.. Enumerable.Range(original.Count, tracks.Count)]);
    }

    /// <summary>Desfazer: com backup, restaura o original; sem backup, remove as faixas adicionadas com novo remux.</summary>
    public void Undo(JournalOperation operation)
    {
        if (operation.From.Length > 0 && File.Exists(operation.From))
        {
            if (File.Exists(operation.To)) _trash(operation.To);
            File.Move(operation.From, operation.To, overwrite: false);
            DeleteEmptyWorkFolders(operation.From);
            return;
        }

        var tool = ToolPath ?? throw new IOException("Desfazer sem o vídeo original requer o mkvmerge (MKVToolNix).");
        var ids = operation.AddedTrackIds is { Count: > 0 } list
            ? list
            : throw new IOException("O histórico não registra quais faixas remover.");

        var original = MatroskaReader.ReadTracks(operation.To);
        var temp = TempPath(operation.To);
        List<string> arguments = ["--gui-mode", "-o", temp, "--subtitle-tracks", "!" + string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture))), operation.To];
        var result = RunAsync(tool, arguments, temp, null, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded) throw new IOException($"O mkvmerge falhou: {result.ErrorText}");

        if (MatroskaReader.ReadTracks(temp).Count != original.Count - ids.Count)
        {
            File.Delete(temp);
            throw new IOException("O vídeo gerado ao desfazer não tem as faixas esperadas; nada foi alterado.");
        }
        Replace(operation.To, temp, backup: null);
    }

    private async Task<MkvMergeResult> RunAsync(string tool, List<string> arguments, string temp, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        HideWorkFolder(temp);
        try
        {
            var result = await runner.RunAsync(tool, arguments, progress, ct).ConfigureAwait(false);
            if (!result.Succeeded && File.Exists(temp)) File.Delete(temp);
            return result;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>Troca o vídeo pelo gerado. O original vira backup (ou vai para a Lixeira); em falha, volta ao lugar.</summary>
    private void Replace(string video, string temp, string? backup)
    {
        if (backup is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(video, backup, overwrite: false);
        }
        else
        {
            _trash(video);
        }

        try
        {
            File.Move(temp, video, overwrite: false);
        }
        catch when (backup is not null)
        {
            File.Move(backup, video, overwrite: false);
            throw;
        }
    }

    /// <summary>Todas as faixas originais na mesma posição, com mesmo tipo e codec, mais as novas no fim.</summary>
    internal static bool Preserves(IReadOnlyList<MatroskaTrack> original, IReadOnlyList<MatroskaTrack> updated, int added) =>
        updated.Count == original.Count + added &&
        original.All(o => updated[o.Index] is var u && u.Type == o.Type && u.CodecId == o.CodecId) &&
        updated.Skip(original.Count).All(t => t.IsSubtitle);

    internal static IEnumerable<string> TrackArguments(EmbedTrack track)
    {
        yield return "--language";
        yield return "0:" + track.Language.Code;
        yield return "--track-name";
        yield return "0:" + TrackName(track.Language);
        yield return "--default-track-flag";
        yield return "0:no";
        if (track.Language.Forced)
        {
            yield return "--forced-display-flag";
            yield return "0:yes";
        }
        if (track.Language.HearingImpaired)
        {
            yield return "--hearing-impaired-flag";
            yield return "0:yes";
        }
        if (SubtitleText.IsText(Path.GetExtension(track.SubtitlePath)))
        {
            yield return "--sub-charset";
            yield return "0:UTF-8";
        }
        yield return track.SubtitlePath;
    }

    /// <summary>"Português (Brasil)", "English (Forçada)", "English (SDH)".</summary>
    public static string TrackName(SubtitleLanguage language) =>
        SubtitleLanguages.DisplayName(language.Code) + (language.Forced ? " (Forçada)" : language.HearingImpaired ? " (SDH)" : "");

    private static string TempPath(string video) =>
        Path.Combine(WorkDirectory(video), "tmp", Path.GetFileNameWithoutExtension(video) + "." + Guid.NewGuid().ToString("N")[..8] + ".mkv");

    private static string BackupPath(string video)
    {
        var dir = Path.Combine(WorkDirectory(video), "originals");
        var name = Path.GetFileName(video);
        var path = Path.Combine(dir, name);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}");
        return path;
    }

    /// <summary>A pasta de trabalho começa com ponto (oculta no macOS/Linux); no Windows recebe o atributo.</summary>
    private static void HideWorkFolder(string pathInside)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(pathInside)!);
        while (dir is not null && dir.Name != LibraryScanner.WorkFolderName) dir = dir.Parent;
        if (dir is null || !OperatingSystem.IsWindows()) return;
        try
        {
            dir.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteEmptyWorkFolders(string backup)
    {
        try
        {
            var originals = Path.GetDirectoryName(backup)!;
            if (Directory.Exists(originals) && !Directory.EnumerateFileSystemEntries(originals).Any()) Directory.Delete(originals);
            var work = Path.GetDirectoryName(originals)!;
            var tmp = Path.Combine(work, "tmp");
            if (Directory.Exists(tmp) && !Directory.EnumerateFileSystemEntries(tmp).Any()) Directory.Delete(tmp);
            if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Espaço livre do disco que contém o caminho (a montagem mais específica no macOS/Linux).</summary>
    public static long? FreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return drive?.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Gigabytes(double bytes) => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024 * 1024 * 1024):0.0} GB");

    /// <summary>Tamanho dos backups de um lote (para o Histórico).</summary>
    public static long BackupSize(RenameJournal journal) =>
        journal.Operations.Where(o => o.Kind == OperationKind.Embed && o.State == OperationState.Done && o.From.Length > 0 && File.Exists(o.From))
            .Sum(o => new FileInfo(o.From).Length);

    /// <summary>Manda os backups de um lote para a Lixeira; desfazer passa a usar o remux.</summary>
    public void RemoveBackups(RenameJournal journal)
    {
        foreach (var operation in journal.Operations.Where(o => o.Kind == OperationKind.Embed && o.From.Length > 0))
        {
            if (File.Exists(operation.From)) _trash(operation.From);
            DeleteEmptyWorkFolders(operation.From);
            operation.From = "";
        }
    }
}
