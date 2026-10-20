using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaOrganizer.App.Services;
using MediaOrganizer.App.ViewModels;
using MediaOrganizer.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaOrganizer.App;

public sealed partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ServiceRegistration.Build();
            var window = new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() };
            _services.GetRequiredService<ShellServices>().Host = window;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
