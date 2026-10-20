using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Diálogo exibido sobre o conteúdo da janela.</summary>
public interface IDialog
{
    void Cancel();
}

public abstract partial class DialogViewModel<TResult> : ObservableObject, IDialog
{
    private readonly TaskCompletionSource<TResult?> _completion = new();

    public Task<TResult?> Completion => _completion.Task;

    protected void Close(TResult? result) => _completion.TrySetResult(result);

    [RelayCommand]
    public void Cancel() => Close(default);
}

public sealed partial class ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive)
    : DialogViewModel<bool>
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDestructive { get; } = isDestructive;

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed partial class ToastViewModel(string message, bool isError, string? actionText, Func<Task>? action, Action<ToastViewModel> dismiss)
    : ObservableObject
{
    public string Message { get; } = message;
    public bool IsError { get; } = isError;
    public string? ActionText { get; } = actionText;
    public bool HasAction => ActionText is not null;

    [RelayCommand]
    private async Task RunActionAsync()
    {
        dismiss(this);
        if (action is not null) await action();
    }

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}
