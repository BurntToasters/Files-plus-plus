using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace FilesPlusPlus.App.Backdrops;

public sealed class WindowBackdropController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _themeSource;
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _acrylicController;
    private MicaController? _micaController;
    private bool _disposed;

    public WindowBackdropController(Window window, FrameworkElement themeSource)
    {
        _window = window;
        _themeSource = themeSource;
    }

    public bool TryInitialize()
    {
        if (_disposed)
        {
            return false;
        }

        if (DesktopAcrylicController.IsSupported())
        {
            EnsureConfiguration();

            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
                TintColor = Windows.UI.Color.FromArgb(0xFF, 0x22, 0x7D, 0xDA),
                TintOpacity = 0.10f,
                LuminosityOpacity = 0.52f,
                FallbackColor = Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x24, 0x32),
            };

            _acrylicController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_configuration!);
            return true;
        }

        if (MicaController.IsSupported())
        {
            EnsureConfiguration();

            _micaController = new MicaController
            {
                Kind = MicaKind.BaseAlt,
            };

            _micaController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _micaController.SetSystemBackdropConfiguration(_configuration!);
            return true;
        }

        return false;
    }

    private void EnsureConfiguration()
    {
        if (_configuration is not null)
        {
            return;
        }

        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true
        };

        SetThemeFromXaml();
        _window.Activated += Window_Activated;
        _themeSource.ActualThemeChanged += ThemeSource_ActualThemeChanged;
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_configuration is null)
        {
            return;
        }

        // Why: For this app, dimming inactive windows weakens glass contrast and hurts legibility.
        _configuration.IsInputActive = true;
    }

    private void ThemeSource_ActualThemeChanged(FrameworkElement sender, object args)
    {
        SetThemeFromXaml();
    }

    private void SetThemeFromXaml()
    {
        if (_configuration is null)
        {
            return;
        }

        _configuration.Theme = _themeSource.ActualTheme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _window.Activated -= Window_Activated;
        _themeSource.ActualThemeChanged -= ThemeSource_ActualThemeChanged;

        _acrylicController?.Dispose();
        _acrylicController = null;

        _micaController?.Dispose();
        _micaController = null;

        _configuration = null;
    }
}
