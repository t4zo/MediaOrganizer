using System.Runtime.InteropServices;

namespace MediaOrganizer.Core.Execution;

/// <summary>
/// Envia arquivos/pastas para a Lixeira do sistema (nunca apaga definitivamente).
/// Windows: Shell (SHFileOperation com FOF_ALLOWUNDO). macOS: ~/.Trash. Linux: especificação freedesktop.org.
/// </summary>
public static partial class RecycleBin
{
    public static void Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        if (OperatingSystem.IsWindows()) SendWindows(path);
        else if (OperatingSystem.IsMacOS()) MoveInto(Path.Combine(Home, ".Trash"), path);
        else SendFreedesktop(path);
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void SendFreedesktop(string path)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".local", "share");
        var trash = Path.Combine(dataHome, "Trash");
        var target = MoveInto(Path.Combine(trash, "files"), path);

        var info = Path.Combine(trash, "info", Path.GetFileName(target) + ".trashinfo");
        Directory.CreateDirectory(Path.GetDirectoryName(info)!);
        File.WriteAllText(info, $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
    }

    private static string MoveInto(string trashDir, string path)
    {
        Directory.CreateDirectory(trashDir);
        var name = Path.GetFileName(path);
        var target = Path.Combine(trashDir, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(trashDir, $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    private static void SendWindows(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Não foi possível mover para a Lixeira (código {result}): {path}");
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
#pragma warning disable SYSLIB1054 // struct com strings não é suportada pelo LibraryImport sem marshaller customizado
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
#pragma warning restore SYSLIB1054
}
