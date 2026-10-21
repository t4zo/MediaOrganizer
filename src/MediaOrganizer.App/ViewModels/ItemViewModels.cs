using CommunityToolkit.Mvvm.ComponentModel;
using MediaOrganizer.Core.Planning;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Uma linha da prévia: nome atual → novo nome (editável).</summary>
public sealed partial class ItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public ItemViewModel(RenameItem item, string folderPath, Action changed)
    {
        Item = item;
        _changed = changed;
        CurrentName = Path.GetFileName(item.SourcePath);
        NewName = Path.GetFileName(item.TargetPath);
        IsSelected = item.IsChange && !item.NeedsConfirmation;

        var directory = Path.GetDirectoryName(item.SourcePath) ?? "";
        var relative = Path.GetRelativePath(folderPath, directory);
        Location = relative is "." || item.Kind == ItemKind.Folder ? null : relative.Replace('\\', '/');
    }

    public RenameItem Item { get; private set; }

    public string CurrentName { get; }

    /// <summary>Subpasta onde o arquivo está (null quando está na raiz da pasta).</summary>
    public string? Location { get; }

    public bool IsSubtitle => Item.Kind == ItemKind.Subtitle;
    public bool IsFolder => Item.Kind == ItemKind.Folder;
    public bool IsVideo => Item.Kind == ItemKind.Video;

    [ObservableProperty]
    public partial string NewName { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool CanSelect => Item.IsChange;
    public bool IsChange => Item.IsChange;
    public bool IsUnchanged => Item.Status == ItemStatus.Unchanged;
    public bool IsWarning => Item.Status is ItemStatus.Warning or ItemStatus.Skipped;
    public bool IsConflict => Item.Status == ItemStatus.Conflict;
    public bool IsOk => Item.Status == ItemStatus.Ok;
    public string? Message => Item.Message;

    public string StatusText => Item.Status switch
    {
        ItemStatus.Ok => "Será renomeado",
        ItemStatus.Unchanged => "Já está no padrão",
        ItemStatus.Warning => Item.Message ?? "Atenção",
        ItemStatus.Skipped => Item.Message ?? "Ignorado",
        ItemStatus.Conflict => Item.Message ?? "Conflito",
        _ => "",
    };

    partial void OnNewNameChanged(string value)
    {
        var target = Path.Combine(Path.GetDirectoryName(Item.TargetPath)!, value.Trim());
        if (target == Item.TargetPath) return;
        Item = Item.WithTarget(target);
        IsSelected = Item.IsChange;
        _changed();
    }

    partial void OnIsSelectedChanged(bool value) => _changed();

    /// <summary>Atualiza o status após a verificação de conflitos da pasta.</summary>
    internal void Update(RenameItem item)
    {
        var wasChange = Item.IsChange;
        Item = item;
        if (wasChange != item.IsChange) IsSelected = item.IsChange;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Arquivo de lixo de release ou pasta vazia que pode ir para a Lixeira.</summary>
public sealed partial class JunkItemViewModel(string path, bool isDirectory, string folderPath) : ObservableObject
{
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public string Display { get; } = System.IO.Path.GetRelativePath(folderPath, path).Replace('\\', '/') + (isDirectory ? "/" : "");

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}
