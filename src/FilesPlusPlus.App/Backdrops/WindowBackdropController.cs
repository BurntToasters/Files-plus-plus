using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using Windows.UI;

namespace FilesPlusPlus.App.Backdrops;

public sealed class WindowBackdropController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _themeSource;
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _acrylicController;
    private MicaController? _micaController;
    private Color? _accentTint;
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

            _acrylicController = new DesktopAcrylicController();
            ApplyAcrylicTuning(_themeSource.ActualTheme);
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

    public void ApplyAccentTint(Color? accent, ElementTheme theme)
    {
        _accentTint = accent;
        if (_acrylicController is not null)
        {
            ApplyAcrylicTuning(theme);
        }
    }

    private void ApplyAcrylicTuning(ElementTheme theme)
    {
        if (_acrylicController is null)
        {
            return;
        }

        var isDark = theme == ElementTheme.Dark ||
            (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var tint = _accentTint ?? (isDark
            ? Color.FromArgb(0xFF, 0x6A, 0x9A, 0xB4)
            : Color.FromArgb(0xFF, 0x22, 0x7D, 0xDA));

        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = tint;
        // Why: Lower tint + higher luminosity reads as frosted Aero glass instead of teal plastic.
        _acrylicController.TintOpacity = isDark ? 0.08f : 0.14f;
        _acrylicController.LuminosityOpacity = isDark ? 0.36f : 0.62f;
        _acrylicController.FallbackColor = isDark
            ? Color.FromArgb(0xFF, 0x0E, 0x14, 0x1A)
            : Color.FromArgb(0xFF, 0xD8, 0xE8, 0xF4);
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
        ApplyAcrylicTuning(sender.ActualTheme);
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
