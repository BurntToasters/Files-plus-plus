using FilesPlusPlus.App.ViewModels;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using System.Threading;

namespace FilesPlusPlus.App;

public partial class App : Application
{
    private readonly ServiceProvider _serviceProvider;
    private Window? _window;
    private int _servicesDisposed;

    public App()
    {
        InitializeComponent();
        _serviceProvider = ConfigureServices();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await _serviceProvider.GetRequiredService<IAppSettingsService>().LoadAsync();
        }
        catch
        {
            // Why: Settings corruption or I/O failures must not block window activation.
        }

        _window = _serviceProvider.GetRequiredService<MainWindow>();
        _window.Closed += Window_Closed;
        _window.Activate();
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        await DisposeServicesAsync();
    }

    private async Task DisposeServicesAsync()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _serviceProvider.DisposeAsync();
        }
        catch
        {
            // Why: Shutdown should complete even when disposable services throw.
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<ITabSessionService, TabSessionService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }
}
