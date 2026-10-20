using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaOrganizer.App.ViewModels;

namespace MediaOrganizer.App.Services;

public enum Page
{
    Organize,
    History,
    Settings,
}

/// <summary>
/// Serviços de "casca" usados pelos ViewModels: diálogos sobrepostos, notificações (toast),
/// seletor de pastas, abrir links/pastas e navegação. Mantém os ViewModels sem dependência de janelas.
/// </summary>
public sealed partial class ShellServices : ObservableObject
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(8);

    /// <summary>Janela principal (definida pelo App ao iniciar).</summary>
    public TopLevel? Host { get; set; }

    /// <summary>Diálogo exibido sobre o conteúdo (null = nenhum).</summary>
    [ObservableProperty]
    public partial IDialog? Dialog { get; private set; }

    [ObservableProperty]
    public partial ToastViewModel? Toast { get; private set; }

    public event Action<Page>? NavigationRequested;

    public void Navigate(Page page) => NavigationRequested?.Invoke(page);

    public async Task<TResult?> ShowDialogAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        Dialog = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            Dialog = null;
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        ShowDialogAsync(new ConfirmDialogViewModel(title, message, confirmText, destructive));

    public void ShowToast(string message, bool isError = false, string? actionText = null, Func<Task>? action = null)
    {
        var toast = new ToastViewModel(message, isError, actionText, action, t => { if (Toast == t) Toast = null; });
        Toast = toast;
        _ = DismissLaterAsync(toast);
    }

    private async Task DismissLaterAsync(ToastViewModel toast)
    {
        await Task.Delay(isErrorDuration(toast));
        if (Toast == toast) Toast = null;

        static TimeSpan isErrorDuration(ToastViewModel t) => t.IsError ? ToastDuration * 2 : ToastDuration;
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        if (Host?.StorageProvider is not { CanPickFolder: true } storage) return null;

        var start = startPath is not null && Directory.Exists(startPath)
            ? await storage.TryGetFolderFromPathAsync(startPath)
            : null;

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task OpenUrlAsync(string url)
    {
        if (Host?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
    }

    public async Task RevealFolderAsync(string path)
    {
        if (Host?.Launcher is { } launcher && Directory.Exists(path)) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
