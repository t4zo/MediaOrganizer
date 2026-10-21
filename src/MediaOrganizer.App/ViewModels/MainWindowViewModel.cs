using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace MediaOrganizer.App.ViewModels;

public sealed record NavItem(Page Page, string Title, Avalonia.Media.Geometry Icon);

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    public MainWindowViewModel(IServiceProvider services, ShellServices shell, SettingsStore settings)
    {
        _services = services;
        Shell = shell;
        shell.NavigationRequested += page => SelectedNav = NavItems.First(n => n.Page == page);

        if (string.IsNullOrWhiteSpace(settings.Current.TmdbApiKey))
        {
            CurrentPage = services.GetRequiredService<OnboardingViewModel>();
            IsOnboarding = true;
        }
        else
        {
            SelectedNav = NavItems[0];
        }
    }

    public ShellServices Shell { get; }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(Page.Organize, "Organizar", Icons.Folder),
        new(Page.Collection, "Coleção", Icons.Checklist),
        new(Page.History, "Histórico", Icons.History),
        new(Page.Settings, "Configurações", Icons.Settings),
    ];

    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial object? CurrentPage { get; private set; }

    [ObservableProperty]
    public partial bool IsOnboarding { get; private set; }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        IsOnboarding = false;
        CurrentPage = value.Page switch
        {
            Page.Collection => _services.GetRequiredService<CollectionViewModel>(),
            Page.History => _services.GetRequiredService<HistoryViewModel>().Refreshed(),
            Page.Settings => _services.GetRequiredService<SettingsViewModel>().Reloaded(),
            _ => _services.GetRequiredService<OrganizeViewModel>(),
        };
    }

    /// <summary>Esc fecha o diálogo aberto.</summary>
    [RelayCommand]
    private void CloseDialog() => Shell.Dialog?.Cancel();
}
