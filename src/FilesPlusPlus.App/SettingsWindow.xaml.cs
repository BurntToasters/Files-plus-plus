using FilesPlusPlus.App.Backdrops;
using FilesPlusPlus.App.ViewModels;
using FilesPlusPlus.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FilesPlusPlus.App;

public sealed partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    private WindowBackdropController? _backdropController;
    private AppWindow? _appWindow;
    private readonly DispatcherQueueTimer _autoSaveTimer;
    private bool _isAutoSaving;
    private bool _isClosePromptActive;
    private bool _allowCloseWithoutPrompt;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        ViewModel.PickFolderAsync = PickFolderAsync;
        _autoSaveTimer = DispatcherQueue.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(900);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        Title = "Files++ Settings";
        _appWindow = GetAppWindow();
        if (_appWindow is not null)
        {
            _appWindow.Resize(new Windows.Graphics.SizeInt32(900, 720));
            _appWindow.Closing += AppWindow_Closing;
            ConfigureWindowChrome();
        }

        _backdropController = new WindowBackdropController(this, RootGrid);
        var backdropApplied = _backdropController.TryInitialize();
        RootGrid.Background = backdropApplied
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];

        ApplyAccentSwatch(ViewModel.AccentHex);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += SettingsWindow_Closed;

        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
    }

    private AppWindow? GetAppWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch
        {
            return null;
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= SettingsWindow_Closed;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_appWindow is not null)
        {
            _appWindow.Closing -= AppWindow_Closing;
            _appWindow = null;
        }
        _backdropController?.Dispose();
        _backdropController = null;
    }

    private void ConfigureWindowChrome()
    {
        if (_appWindow is null)
        {
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _appWindow.TitleBar;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        TitleBarLeftInsetColumn.Width = new GridLength(titleBar.LeftInset / scale);
        TitleBarRightInsetColumn.Width = new GridLength(titleBar.RightInset / scale);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonInactiveForegroundColor = Colors.White;
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x28, 0x6B, 0x92, 0xBC);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x3A, 0x5A, 0x81, 0xA8);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AccentHex))
        {
            ApplyAccentSwatch(ViewModel.AccentHex);
        }

        if (!string.Equals(e.PropertyName, nameof(SettingsViewModel.StatusMessage), StringComparison.Ordinal))
        {
            ScheduleAutoSave();
        }
    }

    private void ScheduleAutoSave()
    {
        if (_isAutoSaving)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void AutoSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isAutoSaving || !ViewModel.HasUnsavedChanges())
        {
            return;
        }

        _isAutoSaving = true;
        try
        {
            await ViewModel.SaveCommand.ExecuteAsync(null);
            if (!string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            {
                ViewModel.StatusMessage = "Settings autosaved.";
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Autosave failed: {ex.Message}";
        }
        finally
        {
            _isAutoSaving = false;
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowCloseWithoutPrompt)
        {
            return;
        }

        if (_isClosePromptActive)
        {
            args.Cancel = true;
            return;
        }

        if (!ViewModel.HasUnsavedChanges())
        {
            return;
        }

        args.Cancel = true;
        _isClosePromptActive = true;

        try
        {
            var dialog = new ContentDialog
            {
                Title = "Unsaved settings",
                Content = "You have unsaved settings changes. Save before closing?",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            ApplyShellDialogChrome(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.SaveCommand.ExecuteAsync(null);
                _allowCloseWithoutPrompt = true;
                Close();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _allowCloseWithoutPrompt = true;
                Close();
            }
        }
        finally
        {
            _isClosePromptActive = false;
        }
    }

    private void ApplyAccentSwatch(string? hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            return;
        }

        AccentSwatch.Background = new SolidColorBrush(color);
        ApplyAccentControlBrushes(color);
    }

    // Why: Settings ToggleSwitch / RadioButton / NavView selection stay on the live accent, not a hardcoded cyan.
    private void ApplyAccentControlBrushes(Color color)
    {
        var opaque = Color.FromArgb(0xFF, color.R, color.G, color.B);
        var mid = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R + 40, 0, 255),
            (byte)Math.Clamp(color.G + 30, 0, 255),
            (byte)Math.Clamp(color.B + 20, 0, 255));
        var dark = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R - 30, 0, 255),
            (byte)Math.Clamp(color.G - 25, 0, 255),
            (byte)Math.Clamp(color.B - 20, 0, 255));

        void SetBrush(ResourceDictionary resources, string key, Color value)
            => resources[key] = new SolidColorBrush(value);

        if (SettingsContentHost?.Resources is { } contentResources)
        {
            SetBrush(contentResources, "ToggleSwitchFillOn", opaque);
            SetBrush(contentResources, "ToggleSwitchFillOnPointerOver", mid);
            SetBrush(contentResources, "ToggleSwitchFillOnPressed", dark);
            SetBrush(contentResources, "ToggleSwitchStrokeOn", opaque);
            SetBrush(contentResources, "ToggleSwitchStrokeOnPointerOver", mid);
            SetBrush(contentResources, "ToggleSwitchStrokeOnPressed", dark);
            SetBrush(contentResources, "RadioButtonOuterEllipseCheckedStroke", opaque);
            SetBrush(contentResources, "RadioButtonOuterEllipseCheckedFill", Color.FromArgb(0x28, opaque.R, opaque.G, opaque.B));
            SetBrush(contentResources, "RadioButtonCheckGlyphFill", opaque);
            SetBrush(contentResources, "RadioButtonOuterEllipseCheckedStrokePointerOver", mid);
            SetBrush(contentResources, "RadioButtonCheckGlyphFillPointerOver", mid);
        }

        if (SettingsNav?.Resources is { } navResources)
        {
            SetBrush(navResources, "NavigationViewItemBackgroundPointerOver", Color.FromArgb(0x34, opaque.R, opaque.G, opaque.B));
            SetBrush(navResources, "NavigationViewItemBackgroundPressed", Color.FromArgb(0x48, opaque.R, opaque.G, opaque.B));
            SetBrush(navResources, "NavigationViewItemBackgroundSelected", Color.FromArgb(0x58, opaque.R, opaque.G, opaque.B));
            SetBrush(navResources, "NavigationViewItemBackgroundSelectedPointerOver", Color.FromArgb(0x70, mid.R, mid.G, mid.B));
            SetBrush(navResources, "NavigationViewItemBackgroundSelectedPressed", Color.FromArgb(0x80, dark.R, dark.G, dark.B));
        }
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var trimmed = hex.Trim().TrimStart('#');
        if (trimmed.Length == 6)
        {
            trimmed = "FF" + trimmed;
        }

        if (trimmed.Length != 8 ||
            !byte.TryParse(trimmed[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var a) ||
            !byte.TryParse(trimmed.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(trimmed.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(trimmed.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ViewPanel.Visibility = tag == "view" ? Visibility.Visible : Visibility.Collapsed;
        NavigationPanel.Visibility = tag == "navigation" ? Visibility.Visible : Visibility.Collapsed;
        SidebarPanel.Visibility = tag == "sidebar" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AccentPicker_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Box,
            IsAlphaEnabled = false,
            IsHexInputVisible = true,
            IsColorSliderVisible = true
        };

        if (TryParseColor(ViewModel.AccentHex, out var current))
        {
            picker.Color = current;
        }

        var dialog = new ContentDialog
        {
            Title = "Pick accent color",
            PrimaryButtonText = "Use color",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = picker,
            XamlRoot = RootGrid.XamlRoot
        };
        ApplyShellDialogChrome(dialog);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var c = picker.Color;
            ViewModel.AccentHex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    private async void StartupPath_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.StartupPath = path;
            ViewModel.IsStartupSpecific = true;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // Why: ContentDialog defaults to Fluent acrylic; re-skin to match shell glass surfaces.
    private void ApplyShellDialogChrome(ContentDialog dialog)
    {
        dialog.RequestedTheme = RootGrid.ActualTheme;
        dialog.CornerRadius = new CornerRadius(8);
        dialog.BorderThickness = new Thickness(1);
        dialog.BorderBrush = TryGetThemeBrush("ShellSurfaceBorderBrush");
        dialog.Background = TryGetThemeBrush("ShellSurfaceBackgroundBrush");
        dialog.Foreground = TryGetThemeBrush("ShellButtonForegroundBrush");

        dialog.Resources["ContentDialogBackground"] = TryGetThemeBrush("ShellSurfaceBackgroundBrush");
        dialog.Resources["ContentDialogBorderBrush"] = TryGetThemeBrush("ShellSurfaceBorderBrush");
        dialog.Resources["ContentDialogForeground"] = TryGetThemeBrush("ShellButtonForegroundBrush");
        dialog.Resources["ContentDialogTopOverlay"] = TryGetThemeBrush("ShellPanelTopSheenBrush");
        dialog.Resources["ContentDialogSmokeFillColor"] = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
    }

    private static Brush TryGetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
