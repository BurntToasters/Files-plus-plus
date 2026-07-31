using FilesPlusPlus.App.Backdrops;
using FilesPlusPlus.App.Models;
using FilesPlusPlus.App.ViewModels;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Graphics;
using Windows.Storage;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FilesPlusPlus.App;

public sealed partial class MainWindow : Window
{
    private const double DefaultDetailsPaneWidth = 340;
    private const double DetailsPaneMinWidth = 250;
    private const double DetailsPaneSplitterWidth = 6;
    private const double DetailsPaneKeyboardResizeStep = 16;
    private const double DetailsPaneKeyboardResizeLargeStep = 48;
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int SwShow = 5;
    private static readonly TimeSpan SyncToastDuration = TimeSpan.FromSeconds(2.4);

    private bool _startupInitialized;
    private bool _startupInitializing;
    private AppWindow? _appWindow;
    private bool _allowCloseWithoutPersist;
    private bool _persistingOnClose;
    private ExplorerTabViewModel? _subscribedTab;
    private bool _detailsPaneResizeActive;
    private double _detailsPaneResizeStartX;
    private double _detailsPaneResizeStartWidth;
    private bool _isDetailsPaneVisible = true;
    private double _lastVisibleDetailsPaneWidth = DefaultDetailsPaneWidth;
    private readonly DispatcherQueueTimer _syncToastTimer;
    private bool _isShortcutsDialogOpen;
    private InputNonClientPointerSource? _nonClientPointerSource;
    private WindowBackdropController? _backdropController;
    private SettingsWindow? _settingsWindow;

    public MainWindowViewModel ViewModel { get; }

    private readonly IAppSettingsService _appSettingsService;
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(MainWindowViewModel viewModel, IAppSettingsService appSettingsService, IServiceProvider serviceProvider)
    {
        ViewModel = viewModel;
        _appSettingsService = appSettingsService;
        _serviceProvider = serviceProvider;
        InitializeComponent();
        ConfigurePreviewMediaTransport();
        RootGrid.DataContext = ViewModel;
        ViewModel.ResolveFileConflictAsync = ResolveFileConflictAsync;
        ConfigureSystemBackdrop();
        ApplyAppSettings(_appSettingsService.Current);
        _appSettingsService.Changed += AppSettings_Changed;

        _syncToastTimer = DispatcherQueue.CreateTimer();
        _syncToastTimer.Interval = SyncToastDuration;
        _syncToastTimer.IsRepeating = false;
        _syncToastTimer.Tick += SyncToastTimer_Tick;

        CompactFileList.ContextFlyout = FileList.ContextFlyout;
        IconFileGrid.ContextFlyout = FileList.ContextFlyout;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateTabSubscription(ViewModel.SelectedTab);
        UpdateViewModeButtons();
        UpdateDetailsPaneToggleButton();

        _appWindow = GetAppWindow();
        ConfigureWindowChrome();
        if (_appWindow is not null)
        {
            _appWindow.Closing += AppWindow_Closing;
        }

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        RootGrid.KeyDown += MainWindow_KeyDown;
        TitleBarHost.SizeChanged += TitleBarHost_SizeChanged;
        TitleBarDragRegion.SizeChanged += TitleBarDragRegion_SizeChanged;

        QueueStartupInitialization();
    }

    private void ConfigureSystemBackdrop()
    {
        try
        {
            _backdropController?.Dispose();
            _backdropController = new WindowBackdropController(this, RootGrid);
            var backdropApplied = _backdropController.TryInitialize();

            // Why: Transparent fallback would render as near-black when system backdrop APIs are unavailable.
            RootGrid.Background = backdropApplied
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                : (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
        catch
        {
            RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
    }

    // Why: Nested MediaTransportControls in XAML crashes WinUI XamlCompiler; configure compact chrome in code.
    private void ConfigurePreviewMediaTransport()
    {
        PreviewMediaPlayer.TransportControls = new MediaTransportControls
        {
            IsCompact = true,
            IsZoomButtonVisible = false,
            IsZoomEnabled = false,
            IsPlaybackRateButtonVisible = false,
            IsRepeatButtonVisible = false,
            IsSkipBackwardButtonVisible = false,
            IsSkipForwardButtonVisible = false
        };
    }

    private void ConfigureWindowChrome()
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.Title = "Files++";
        ExtendsContentIntoTitleBar = true;
        // Why: Host-level title bar keeps brand/empty space draggable while tabs and + remain clickable.
        SetTitleBar(TitleBarHost);
        _nonClientPointerSource = TryCreateNonClientPointerSource();

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _appWindow.TitleBar;
        // Why: Standard caption height keeps the title strip compact; Tall made chrome feel oversized.
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        _appWindow.Changed -= AppWindow_Changed;
        _appWindow.Changed += AppWindow_Changed;

        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;

        UpdateTitleBarLayout();
    }

    private InputNonClientPointerSource? TryCreateNonClientPointerSource()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return InputNonClientPointerSource.GetForWindowId(windowId);
        }
        catch
        {
            return null;
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarLayout();
    }

    private void TitleBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarLayout();
    }

    private void TitleBarDragRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarDragRegion();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!DispatcherQueue.TryEnqueue(UpdateTitleBarLayout))
        {
            UpdateTitleBarLayout();
        }
    }

    private void UpdateTitleBarLayout()
    {
        if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _appWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        TitleBarLeftInsetColumn.Width = new GridLength(titleBar.LeftInset / scale);
        TitleBarRightInsetColumn.Width = new GridLength(Math.Max(titleBar.RightInset / scale, 138));

        // Why: Match custom title row to the live caption height so glass sits flush under min/max/close.
        var captionHeight = titleBar.Height / scale;
        if (captionHeight < 28)
        {
            captionHeight = 32;
        }

        TitleBarRowDefinition.Height = new GridLength(captionHeight);
        ApplyTitleBarButtonColors(titleBar);
        UpdateTitleBarDragRegion();
    }

    private void UpdateTitleBarDragRegion()
    {
        if (_nonClientPointerSource is null)
        {
            return;
        }

        try
        {
            // Why: Only the empty strip is a non-client caption; interactive tab/brand controls stay client-side.
            if (TryGetWindowRect(TitleBarDragRegion, out var dragRegion))
            {
                _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption, [dragRegion]);
            }
            else
            {
                _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption, Array.Empty<RectInt32>());
            }
        }
        catch
        {
            // Why: Pointer-region updates race during resize and should not break window interaction.
        }
    }

    private static bool TryGetWindowRect(FrameworkElement element, out RectInt32 rect)
    {
        rect = default;
        if (element.XamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var scale = element.XamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var transform = element.TransformToVisual(null);
            var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            var x = (int)Math.Round(origin.X * scale);
            var y = (int)Math.Round(origin.Y * scale);
            var width = (int)Math.Round(element.ActualWidth * scale);
            var height = (int)Math.Round(element.ActualHeight * scale);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            rect = new RectInt32(x, y, width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTitleBarButtonColors(AppWindowTitleBar titleBar)
    {
        var buttonForeground = TryGetThemeColor(
            "ShellTitleBarForegroundBrush",
            Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xF8, 0xFC));
        var buttonInactiveForeground = TryGetThemeColor(
            "ShellTitleBarButtonInactiveForegroundBrush",
            Windows.UI.Color.FromArgb(0x90, 0xB0, 0xC0, 0xD0));
        var buttonHoverBackground = TryGetThemeColor(
            "ShellTitleBarButtonHoverBackgroundBrush",
            Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        var buttonPressedBackground = TryGetThemeColor(
            "ShellTitleBarButtonPressedBackgroundBrush",
            Windows.UI.Color.FromArgb(0x70, 0xA8, 0xD4, 0xE8));

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = buttonForeground;
        titleBar.ButtonInactiveForegroundColor = buttonInactiveForeground;
        titleBar.ButtonHoverBackgroundColor = buttonHoverBackground;
        titleBar.ButtonHoverForegroundColor = buttonForeground;
        titleBar.ButtonPressedBackgroundColor = buttonPressedBackground;
        titleBar.ButtonPressedForegroundColor = buttonForeground;
    }

    private Windows.UI.Color TryGetThemeColor(string brushResourceKey, Windows.UI.Color fallback)
    {
        if (TryFindResourceBrush(brushResourceKey) is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }

    private SolidColorBrush? TryFindResourceBrush(string resourceKey)
    {
        if (RootGrid.Resources.TryGetValue(resourceKey, out var rootValue) && rootValue is SolidColorBrush rootBrush)
        {
            return rootBrush;
        }

        var themeKey = RootGrid.ActualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light"
        };

        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var themeDictObj) &&
            themeDictObj is ResourceDictionary themeDict &&
            themeDict.TryGetValue(resourceKey, out var themeValue) &&
            themeValue is SolidColorBrush themeBrush)
        {
            return themeBrush;
        }

        if (Application.Current.Resources.ThemeDictionaries.TryGetValue("Default", out var defaultDictObj) &&
            defaultDictObj is ResourceDictionary defaultDict &&
            defaultDict.TryGetValue(resourceKey, out var defaultValue) &&
            defaultValue is SolidColorBrush defaultBrush)
        {
            return defaultBrush;
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out var appValue) && appValue is SolidColorBrush appBrush)
        {
            return appBrush;
        }

        return null;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        QueueStartupInitialization();
    }

    private void QueueStartupInitialization()
    {
        if (_startupInitialized || _startupInitializing)
        {
            return;
        }

        _startupInitializing = true;
        if (!DispatcherQueue.TryEnqueue(async () => await EnsureStartupInitializedAsync()))
        {
            _ = EnsureStartupInitializedAsync();
        }
    }

    private async Task EnsureStartupInitializedAsync()
    {
        if (_startupInitialized)
        {
            return;
        }

        try
        {
            ViewModel.StatusLine = "Initializing...";
            await ViewModel.InitializeAsync();

            if (ViewModel.Tabs.Count == 0)
            {
                await ViewModel.AddTabAsync();
            }

            ApplyWindowLayout(ViewModel.StartupLayout);
            ApplySelectedTabDetailsPaneState();
        }
        catch (Exception ex)
        {
            ViewModel.StatusLine = $"Startup issue: {ex.Message}";

            if (ViewModel.Tabs.Count > 0)
            {
                return;
            }

            var fallbackPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(fallbackPath) || !Directory.Exists(fallbackPath))
            {
                fallbackPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            try
            {
                await ViewModel.AddTabAsync(fallbackPath);
            }
            catch (Exception fallbackEx)
            {
                ViewModel.StatusLine = $"Startup fallback issue: {fallbackEx.Message}";
            }
        }
        finally
        {
            _startupInitialized = true;
            _startupInitializing = false;
            Activated -= MainWindow_Activated;
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowCloseWithoutPersist)
        {
            return;
        }

        args.Cancel = true;

        if (_persistingOnClose)
        {
            return;
        }

        _persistingOnClose = true;

        try
        {
            await ViewModel.PersistSessionAsync(ReadWindowLayout());
        }
        catch (Exception ex)
        {
            ViewModel.StatusLine = $"Session save issue: {ex.Message}";
        }
        finally
        {
            _persistingOnClose = false;
            _allowCloseWithoutPersist = true;

            if (!DispatcherQueue.TryEnqueue(Close))
            {
                Close();
            }
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_appWindow is not null)
        {
            _appWindow.Closing -= AppWindow_Closing;
            _appWindow.Changed -= AppWindow_Changed;
            _appWindow = null;
        }

        _syncToastTimer.Stop();
        _syncToastTimer.Tick -= SyncToastTimer_Tick;

        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        UpdateTabSubscription(null);
        RootGrid.KeyDown -= MainWindow_KeyDown;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        TitleBarHost.SizeChanged -= TitleBarHost_SizeChanged;
        TitleBarDragRegion.SizeChanged -= TitleBarDragRegion_SizeChanged;
        _nonClientPointerSource = null;
        _backdropController?.Dispose();
        _backdropController = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedTab))
        {
            return;
        }

        UpdateTabSubscription(ViewModel.SelectedTab);
        UpdateViewModeButtons();
        ApplySelectedTabDetailsPaneState();
    }

    private void UpdateTabSubscription(ExplorerTabViewModel? nextTab)
    {
        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged -= SelectedTab_PropertyChanged;
        }

        _subscribedTab = nextTab;

        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged += SelectedTab_PropertyChanged;
        }
    }

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerTabViewModel.ViewState))
        {
            UpdateViewModeButtons();
            ApplySelectedTabDetailsPaneState();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = IsModifierDown(VirtualKey.Control);
        var shiftDown = IsModifierDown(VirtualKey.Shift);
        var altDown = IsModifierDown(VirtualKey.Menu);

        if (e.Key == VirtualKey.F1)
        {
            _ = ShowKeyboardShortcutsDialogAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F6)
        {
            CyclePaneFocus(reverse: shiftDown);
            e.Handled = true;
            return;
        }

        if (ViewModel.SelectedTab is null)
        {
            return;
        }

        if (controlDown && shiftDown && e.Key == VirtualKey.D)
        {
            FocusDetailsPaneSplitter();
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.A && !IsSearchBoxFocused())
        {
            var activeList = GetActiveFileListControl();
            activeList.SelectAll();
            ViewModel.UpdateSelection(activeList.SelectedItems.OfType<FileItem>());
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.C && !IsSearchBoxFocused())
        {
            RunUiAction("Copy selection", () => ViewModel.CopySelectionAsync());
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.X && !IsSearchBoxFocused())
        {
            RunUiAction("Cut selection", () => ViewModel.CutSelectionAsync());
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.V && !IsSearchBoxFocused())
        {
            RunUiAction("Paste", () => ViewModel.PasteIntoCurrentAsync());
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Delete && !IsSearchBoxFocused())
        {
            RunUiAction("Delete selection", DeleteSelectionWithConfirmAsync);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F2 && !IsSearchBoxFocused())
        {
            RunUiAction("Rename selection", RenameSelectedItemAsync);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            RunUiAction("Refresh", () => ViewModel.RefreshAsync());
            e.Handled = true;
            return;
        }

        if (altDown && e.Key == VirtualKey.Up)
        {
            RunUiAction("Navigate up", () => ViewModel.NavigateUpAsync());
            e.Handled = true;
            return;
        }

        if (altDown && e.Key == VirtualKey.Enter && !IsSearchBoxFocused())
        {
            OpenSelectedProperties();
            e.Handled = true;
            return;
        }

        var selectedItem = GetActiveFileListControl().SelectedItem as FileItem;
        if (e.Key == VirtualKey.Enter && !IsSearchBoxFocused() && selectedItem is not null)
        {
            RunUiAction("Open item", () => ViewModel.OpenItemAsync(selectedItem));
            e.Handled = true;
        }
    }

    private void CyclePaneFocus(bool reverse)
    {
        var focusTargets = GetPaneFocusTargets();
        if (focusTargets.Count == 0)
        {
            return;
        }

        var currentFocused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        var currentIndex = FindFocusedPaneIndex(focusTargets, currentFocused);
        var nextIndex = reverse
            ? (currentIndex <= 0 ? focusTargets.Count - 1 : currentIndex - 1)
            : (currentIndex < 0 || currentIndex >= focusTargets.Count - 1 ? 0 : currentIndex + 1);
        var nextTarget = focusTargets[nextIndex];

        if (!nextTarget.Focus(FocusState.Keyboard))
        {
            DispatcherQueue.TryEnqueue(() => nextTarget.Focus(FocusState.Keyboard));
        }

        ViewModel.StatusLine = $"Focus: {GetPaneFocusLabel(nextTarget)} pane.";
    }

    private List<Control> GetPaneFocusTargets()
    {
        var targets = new List<Control>();

        if (NavigationTreeView.Visibility == Visibility.Visible && NavigationTreeView.IsEnabled)
        {
            targets.Add(NavigationTreeView);
        }

        var activeFileList = GetActiveFileListControl();
        if (activeFileList.Visibility == Visibility.Visible && activeFileList.IsEnabled)
        {
            targets.Add(activeFileList);
        }

        if (_isDetailsPaneVisible &&
            DetailsPaneHost.Visibility == Visibility.Visible &&
            DetailsPaneScrollViewer.IsEnabled)
        {
            targets.Add(DetailsPaneScrollViewer);
        }

        return targets;
    }

    private static int FindFocusedPaneIndex(IReadOnlyList<Control> targets, DependencyObject? focusedElement)
    {
        if (focusedElement is null)
        {
            return -1;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            if (IsSameOrDescendantOf(focusedElement, targets[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsSameOrDescendantOf(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private string GetPaneFocusLabel(Control pane)
    {
        if (ReferenceEquals(pane, NavigationTreeView))
        {
            return "Navigation";
        }

        if (ReferenceEquals(pane, DetailsPaneScrollViewer))
        {
            return "Details";
        }

        return "Files";
    }

    private void FocusDetailsPaneSplitter()
    {
        if (!_isDetailsPaneVisible)
        {
            SetDetailsPaneVisibility(true);
        }

        if (!DetailsPaneSplitter.Focus(FocusState.Keyboard))
        {
            DispatcherQueue.TryEnqueue(() => DetailsPaneSplitter.Focus(FocusState.Keyboard));
        }

        ViewModel.StatusLine = "Focused details pane splitter. Use Left and Right arrows to resize.";
    }

    // Section: Tab bar

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExplorerTabViewModel tab)
        {
            ViewModel.SelectedTab = tab;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExplorerTabViewModel tab)
        {
            RunUiAction("Close tab", () => ViewModel.CloseTabAsync(tab));
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Add tab", () => ViewModel.AddTabAsync());
    }

    private void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Create folder", () => ViewModel.CreateFolderAsync());
    }

    private void NewSplitButton_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Create folder", () => ViewModel.CreateFolderAsync());
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Copy selection", () => ViewModel.CopySelectionAsync());
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Cut selection", () => ViewModel.CutSelectionAsync());
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Paste", () => ViewModel.PasteIntoCurrentAsync());
    }

    private void DetailsViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCurrentViewMode(FolderViewMode.Details);
        UpdateViewModeButtons();
    }

    private void CompactViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCurrentViewMode(FolderViewMode.Compact);
        UpdateViewModeButtons();
    }

    private void IconsViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCurrentViewMode(FolderViewMode.Icons);
        UpdateViewModeButtons();
    }

    private void DetailsPaneToggleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDetailsPaneVisibility(DetailsPaneToggleItem.IsChecked);
    }

    private void NewTextFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Create file", () => ViewModel.CreateFileAsync());
    }

    private void SortFlyout_Opening(object sender, object e)
    {
        var viewState = ViewModel.SelectedTab?.ViewState;
        if (viewState is null)
        {
            return;
        }

        SortNameRadio.IsChecked = viewState.SortColumn == SortColumn.Name;
        SortTypeRadio.IsChecked = viewState.SortColumn == SortColumn.Type;
        SortDateRadio.IsChecked = viewState.SortColumn == SortColumn.DateModified;
        SortSizeRadio.IsChecked = viewState.SortColumn == SortColumn.Size;

        SortAscRadio.IsChecked = viewState.SortDirection == SortDirection.Ascending;
        SortDescRadio.IsChecked = viewState.SortDirection == SortDirection.Descending;

        GroupFoldersToggle.IsChecked = viewState.GroupDirectoriesFirst;
    }

    private void SortColumnRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not string tag ||
            !Enum.TryParse<SortColumn>(tag, ignoreCase: true, out var column))
        {
            return;
        }

        var direction = ViewModel.SelectedTab?.ViewState.SortDirection ?? SortDirection.Ascending;
        ViewModel.SetSort(column, direction);
    }

    private void SortDirectionRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not string tag ||
            !Enum.TryParse<SortDirection>(tag, ignoreCase: true, out var direction))
        {
            return;
        }

        var column = ViewModel.SelectedTab?.ViewState.SortColumn ?? SortColumn.Name;
        ViewModel.SetSort(column, direction);
    }

    private void GroupFoldersToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetDirectoryGrouping(GroupFoldersToggle.IsChecked);
    }

    private void SyncPaneLayout_Click(object sender, RoutedEventArgs e)
    {
        var syncMessage = ViewModel.ApplyCurrentDetailsPaneStateToAllTabs();
        ApplySelectedTabDetailsPaneState();
        ShowSyncToast(syncMessage);
    }

    private async void ShortcutsHelp_Click(object sender, RoutedEventArgs e)
    {
        await ShowKeyboardShortcutsDialogAsync();
    }

    private async Task ShowKeyboardShortcutsDialogAsync()
    {
        if (_isShortcutsDialogOpen)
        {
            return;
        }

        _isShortcutsDialogOpen = true;
        try
        {
            var shortcutsText = new TextBlock
            {
                Text =
                    """
                    General
                      F1                  Show this shortcuts help
                      F6                  Next pane focus (Sidebar -> Files -> Details)
                      Shift+F6            Previous pane focus

                    Navigation and files
                      Ctrl+A              Select all
                      Ctrl+C / Ctrl+X     Copy / Cut
                      Ctrl+V              Paste
                      Delete              Delete selection
                      F2                  Rename selection
                      F5                  Refresh current folder
                      Alt+Up              Go to parent folder
                      Enter               Open selected item

                    Details pane
                      Ctrl+Shift+D        Focus details pane splitter
                      Left / Right        Resize details pane
                      Shift+Left / Right  Resize details pane (large step)
                      Home / End          Min / Max details pane width
                    """,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Cascadia Mono")
            };

            var dialog = new ContentDialog
            {
                Title = "Keyboard Shortcuts",
                Content = shortcutsText,
                PrimaryButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            ApplyShellDialogChrome(dialog);

            await dialog.ShowAsync();
        }
        finally
        {
            _isShortcutsDialogOpen = false;
        }
    }

    private async Task<MainWindowViewModel.FileConflictResolution> ResolveFileConflictAsync(FileConflictPrompt prompt)
    {
        var sourceName = Path.GetFileName(prompt.SourcePath);
        var destinationName = Path.GetFileName(prompt.DestinationPath);
        var itemType = prompt.IsDirectory ? "folder" : "file";

        var message =
            $"A {itemType} named \"{destinationName}\" already exists in destination.\n\n" +
            $"Source: {sourceName}\n" +
            $"Destination: {prompt.DestinationPath}\n\n" +
            "Choose how to continue:";

        var dialog = new ContentDialog
        {
            Title = "File conflict",
            Content = message,
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Keep both",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        ApplyShellDialogChrome(dialog);

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => MainWindowViewModel.FileConflictResolution.Replace,
            ContentDialogResult.Secondary => MainWindowViewModel.FileConflictResolution.KeepBoth,
            _ => MainWindowViewModel.FileConflictResolution.Skip
        };
    }

    private async void OpenOperationsQueue_Click(object sender, RoutedEventArgs e)
    {
        await ShowOperationQueueInspectorAsync();
    }

    private async Task ShowOperationQueueInspectorAsync()
    {
        var entries = ViewModel.FileOperationHistory.ToList();
        var panel = new StackPanel
        {
            Spacing = 8
        };

        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No queued operations yet.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var entry in entries.Take(40))
            {
                var stateText = entry.State.ToString().ToUpperInvariant();
                var sourceName = Path.GetFileName(entry.SourcePath);
                var destinationName = Path.GetFileName(entry.DestinationPath);
                var summary = $"{stateText}  {entry.Type}: {sourceName} -> {destinationName}";
                if (!string.IsNullOrWhiteSpace(entry.ErrorMessage))
                {
                    summary += $"\nError: {entry.ErrorMessage}";
                }

                panel.Children.Add(new TextBlock
                {
                    Text = summary,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                });
            }
        }

        var scroll = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 420
        };

        var dialog = new ContentDialog
        {
            Title = $"Operations queue  (P:{ViewModel.PendingOperationsCount} R:{ViewModel.RunningOperationsCount} F:{ViewModel.FailedOperationsCount})",
            Content = scroll,
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ApplyShellDialogChrome(dialog);

        await dialog.ShowAsync();
    }

    // Section: Toolbar

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Navigate back", () => ViewModel.NavigateBackAsync());
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Navigate forward", () => ViewModel.NavigateForwardAsync());
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Navigate up", () => ViewModel.NavigateUpAsync());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Refresh", () => ViewModel.RefreshAsync());
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbSegment segment)
        {
            RunUiAction("Navigate breadcrumb", () => ViewModel.NavigateToLocationAsync(segment.FullPath));
        }
    }

    private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        RunUiAction("Search", () => ViewModel.SearchCurrentTabAsync(args.QueryText ?? string.Empty));
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
        {
            RunUiAction("Clear search", () => ViewModel.SearchCurrentTabAsync(string.Empty));
        }
    }

    // Section: Sidebar navigation tree

    private void NavigationTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is NavigationNode node)
        {
            RunUiAction("Load folder tree", () => ViewModel.LoadNavigationNodeChildrenAsync(node));
        }
    }

    private void NavigationTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not NavigationNode node)
        {
            return;
        }

        if (node.FullPath == MainWindowViewModel.ThisPcVirtualPath)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        if (node.IsNavigable)
        {
            RunUiAction("Navigate tree", () => ViewModel.NavigateToLocationAsync(node.FullPath!));
        }
    }

    // Section: File list

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase list)
        {
            var selected = list.SelectedItems.OfType<FileItem>();
            ViewModel.UpdateSelection(selected);
        }
    }

    private void FileList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_appSettingsService.Current.Navigation.OpenItemMode == AppOpenItemMode.SingleClick)
        {
            return;
        }

        if (sender is ListViewBase list && list.SelectedItem is FileItem item)
        {
            RunUiAction("Open item", () => ViewModel.OpenItemAsync(item));
        }
    }

    private void FileList_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_appSettingsService.Current.Navigation.OpenItemMode != AppOpenItemMode.SingleClick)
        {
            return;
        }

        var item = ResolveFileItemFromOriginalSource(e.OriginalSource);
        if (item is null)
        {
            return;
        }

        RunUiAction("Open item", () => ViewModel.OpenItemAsync(item));
    }

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase sourceList)
        {
            return;
        }

        var item = ResolveFileItemFromOriginalSource(e.OriginalSource);
        if (item is null)
        {
            sourceList.SelectedItems.Clear();
            ViewModel.UpdateSelection(Array.Empty<FileItem>());
            return;
        }

        var selectedItems = sourceList.SelectedItems.OfType<FileItem>().ToList();
        var isAlreadySelected = selectedItems.Contains(item);
        if (!isAlreadySelected)
        {
            sourceList.SelectedItems.Clear();
            sourceList.SelectedItem = item;
        }

        ViewModel.UpdateSelection(sourceList.SelectedItems.OfType<FileItem>());
    }

    private void FileListContextFlyout_Opening(object sender, object e)
    {
        var selectedCount = ViewModel.SelectedTab?.SelectedItems.Count ?? 0;

        OpenMenuItem.IsEnabled = selectedCount == 1;
        CopyMenuItem.IsEnabled = selectedCount > 0;
        CutMenuItem.IsEnabled = selectedCount > 0;
        RenameMenuItem.IsEnabled = selectedCount == 1;
        DeleteMenuItem.IsEnabled = selectedCount > 0;
        PropertiesMenuItem.IsEnabled = selectedCount == 1;
        PasteMenuItem.IsEnabled = ViewModel.CanPasteFromClipboard();
    }

    private void OpenSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTab?.SelectedItems.Count == 1)
        {
            var item = ViewModel.SelectedTab.SelectedItems[0];
            RunUiAction("Open item", () => ViewModel.OpenItemAsync(item));
        }
    }

    private void CopySelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Copy selection", () => ViewModel.CopySelectionAsync());
    }

    private void CutSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Cut selection", () => ViewModel.CutSelectionAsync());
    }

    private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Paste", () => ViewModel.PasteIntoCurrentAsync());
    }

    private void RenameSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Rename selection", RenameSelectedItemAsync);
    }

    private void DeleteSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Delete selection", DeleteSelectionWithConfirmAsync);
    }

    private async Task DeleteSelectionWithConfirmAsync()
    {
        var selection = ViewModel.SelectedTab?.SelectedItems;
        if (selection is null || selection.Count == 0)
        {
            return;
        }

        if (_appSettingsService.Current.Navigation.ConfirmDelete)
        {
            var message = selection.Count == 1
                ? $"Delete '{selection[0].Name}'?"
                : $"Delete {selection.Count} items?";

            var dialog = new ContentDialog
            {
                Title = "Confirm delete",
                Content = message,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            ApplyShellDialogChrome(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.DeleteSelectionAsync();
    }

    private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction("Create folder", () => ViewModel.CreateFolderAsync());
    }

    private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedProperties();
    }

    private void OpenSelectedProperties()
    {
        if (ViewModel.SelectedTab?.SelectedItems.Count != 1)
        {
            ViewModel.StatusLine = "Select a single item to view properties.";
            return;
        }

        var selectedPath = ViewModel.SelectedTab.SelectedItems[0].FullPath;
        if (!File.Exists(selectedPath) && !Directory.Exists(selectedPath))
        {
            ViewModel.StatusLine = "The selected item no longer exists.";
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (ShowNativePropertiesSheet(selectedPath, hwnd))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        ViewModel.StatusLine = error == 0
            ? "Could not open item properties."
            : $"Could not open item properties (Win32 {error}).";
    }

    private async void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListViewBase sourceList)
        {
            e.Cancel = true;
            return;
        }

        var selectedPaths = sourceList.SelectedItems
            .OfType<FileItem>()
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedPaths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var storageItems = await ResolveStorageItemsAsync(selectedPaths);
            if (storageItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            e.Data.SetStorageItems(storageItems);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            ViewModel.StatusLine = $"Drag start failed: {ex.Message}";
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        var operation = ResolveDropOperation(e.DataView);
        e.AcceptedOperation = operation;

        if (operation != DataPackageOperation.None)
        {
            e.DragUIOverride.Caption = operation == DataPackageOperation.Move
                ? "Move to current folder"
                : "Copy to current folder";
            e.DragUIOverride.IsCaptionVisible = true;
        }

        e.Handled = true;
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        var operation = ResolveDropOperation(e.DataView);
        if (operation == DataPackageOperation.None)
        {
            e.Handled = true;
            return;
        }

        RunUiAction(
            "Drop items",
            async () =>
            {
                var droppedPaths = await ExtractDroppedPathsAsync(e.DataView);
                if (droppedPaths.Count == 0)
                {
                    ViewModel.StatusLine = "No droppable items found.";
                    return;
                }

                await ViewModel.QueueTransferIntoCurrentAsync(
                    droppedPaths,
                    moveItems: operation == DataPackageOperation.Move);
            });

        e.Handled = true;
    }

    // Section: Sort column headers

    private void SortName_Click(object sender, RoutedEventArgs e) => ViewModel.SortCurrent(SortColumn.Name);
    private void SortType_Click(object sender, RoutedEventArgs e) => ViewModel.SortCurrent(SortColumn.Type);
    private void SortDate_Click(object sender, RoutedEventArgs e) => ViewModel.SortCurrent(SortColumn.DateModified);
    private void SortSize_Click(object sender, RoutedEventArgs e) => ViewModel.SortCurrent(SortColumn.Size);

    // Section: Window layout helpers

    private void ApplyWindowLayout(WindowLayout layout)
    {
        try
        {
            var restoredPaneWidth = layout.DetailsPaneWidth <= 0
                ? DefaultDetailsPaneWidth
                : layout.DetailsPaneWidth;
            SetDetailsPaneVisibility(layout.IsDetailsPaneVisible, restoredPaneWidth, synchronizeTabState: false);

            var appWindow = GetAppWindow();
            if (appWindow is null)
            {
                return;
            }

            var width = Math.Max(900, (int)layout.Width);
            var height = Math.Max(600, (int)layout.Height);
            appWindow.Resize(new SizeInt32(width, height));

            if (layout.IsMaximized && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch
        {
            // Why: Persisted geometry may be invalid across monitor or DPI changes.
        }
    }

    private WindowLayout ReadWindowLayout()
    {
        var detailsPaneWidth = ReadDetailsPaneWidth();
        var isDetailsPaneVisible = _isDetailsPaneVisible;

        try
        {
            var appWindow = GetAppWindow();
            if (appWindow is null)
            {
                return new WindowLayout(1360, 860, IsMaximized: false)
                {
                    DetailsPaneWidth = detailsPaneWidth,
                    IsDetailsPaneVisible = isDetailsPaneVisible
                };
            }

            var isMaximized = appWindow.Presenter is OverlappedPresenter presenter &&
                              presenter.State == OverlappedPresenterState.Maximized;
            return new WindowLayout(appWindow.Size.Width, appWindow.Size.Height, isMaximized)
            {
                DetailsPaneWidth = detailsPaneWidth,
                IsDetailsPaneVisible = isDetailsPaneVisible
            };
        }
        catch
        {
            return new WindowLayout(1360, 860, IsMaximized: false)
            {
                DetailsPaneWidth = detailsPaneWidth,
                IsDetailsPaneVisible = isDetailsPaneVisible
            };
        }
    }

    private double ReadDetailsPaneWidth()
    {
        if (!_isDetailsPaneVisible)
        {
            return ClampDetailsPaneWidth(_lastVisibleDetailsPaneWidth);
        }

        var width = DetailsPaneColumn.ActualWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            width = DetailsPaneColumn.Width.Value;
        }

        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            width = DefaultDetailsPaneWidth;
        }

        return ClampDetailsPaneWidth(width);
    }

    private double ClampDetailsPaneWidth(double width)
    {
        var maxWidth = DetailsPaneColumn.MaxWidth > 0 ? DetailsPaneColumn.MaxWidth : double.MaxValue;
        return Math.Clamp(width, DetailsPaneMinWidth, maxWidth);
    }

    private void SetDetailsPaneVisibility(bool isVisible, double? preferredWidth = null)
    {
        SetDetailsPaneVisibility(isVisible, preferredWidth, synchronizeTabState: true);
    }

    private void SetDetailsPaneVisibility(bool isVisible, double? preferredWidth, bool synchronizeTabState)
    {
        var resolvedWidth = ClampDetailsPaneWidth(preferredWidth ?? _lastVisibleDetailsPaneWidth);

        if (isVisible)
        {
            _isDetailsPaneVisible = true;
            _lastVisibleDetailsPaneWidth = resolvedWidth;

            DetailsPaneHost.Visibility = Visibility.Visible;
            DetailsPaneSplitter.Visibility = Visibility.Visible;
            DetailsPaneSplitterColumn.Width = new GridLength(DetailsPaneSplitterWidth);
            DetailsPaneColumn.MinWidth = DetailsPaneMinWidth;
            DetailsPaneColumn.Width = new GridLength(resolvedWidth);
        }
        else
        {
            if (_isDetailsPaneVisible)
            {
                _lastVisibleDetailsPaneWidth = ReadDetailsPaneWidth();
            }
            else if (preferredWidth is not null)
            {
                _lastVisibleDetailsPaneWidth = resolvedWidth;
            }

            _isDetailsPaneVisible = false;
            DetailsPaneHost.Visibility = Visibility.Collapsed;
            DetailsPaneSplitter.Visibility = Visibility.Collapsed;
            DetailsPaneSplitterColumn.Width = new GridLength(0);
            DetailsPaneColumn.MinWidth = 0;
            DetailsPaneColumn.Width = new GridLength(0);
        }

        if (synchronizeTabState)
        {
            ViewModel.SetCurrentDetailsPaneVisibility(_isDetailsPaneVisible);
            ViewModel.SetCurrentDetailsPaneWidth(_lastVisibleDetailsPaneWidth);
        }

        UpdateDetailsPaneToggleButton();
    }

    private void ApplySelectedTabDetailsPaneState()
    {
        var selectedTab = ViewModel.SelectedTab;
        if (selectedTab is null)
        {
            return;
        }

        SetDetailsPaneVisibility(
            selectedTab.ViewState.IsDetailsPaneVisible,
            selectedTab.ViewState.DetailsPaneWidth,
            synchronizeTabState: false);
    }

    private void UpdateDetailsPaneToggleButton()
    {
        DetailsPaneToggleItem.IsChecked = _isDetailsPaneVisible;
    }

    private AppWindow? GetAppWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private async Task RenameSelectedItemAsync()
    {
        if (ViewModel.SelectedTab is null || ViewModel.SelectedTab.SelectedItems.Count != 1)
        {
            ViewModel.StatusLine = "Select a single item to rename.";
            return;
        }

        var selectedItem = ViewModel.SelectedTab.SelectedItems[0];
        var newName = await PromptForRenameAsync(selectedItem.Name);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        if (ContainsInvalidFileNameChars(newName))
        {
            ViewModel.StatusLine = "The name contains invalid characters.";
            return;
        }

        if (string.Equals(selectedItem.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        await ViewModel.RenameSelectionAsync(newName);
    }

    private void UpdateViewModeButtons()
    {
        var viewMode = ViewModel.SelectedTab?.ViewState.ViewMode ?? FolderViewMode.Details;

        ViewDetailsRadio.IsChecked = viewMode == FolderViewMode.Details;
        ViewCompactRadio.IsChecked = viewMode == FolderViewMode.Compact;
        ViewIconsRadio.IsChecked = viewMode == FolderViewMode.Icons;
    }

    private void DetailsSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDetailsPaneVisible || sender is not UIElement splitter)
        {
            return;
        }

        _detailsPaneResizeActive = true;
        _detailsPaneResizeStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _detailsPaneResizeStartWidth = DetailsPaneColumn.Width.Value;
        splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DetailsSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_detailsPaneResizeActive || !_isDetailsPaneVisible)
        {
            return;
        }

        var currentX = e.GetCurrentPoint(RootGrid).Position.X;
        var delta = _detailsPaneResizeStartX - currentX;
        ResizeDetailsPaneTo(_detailsPaneResizeStartWidth + delta);
        e.Handled = true;
    }

    private void DetailsSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDetailsPaneResize(sender, e);
    }

    private void DetailsSplitter_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndDetailsPaneResize(sender, e);
    }

    private void EndDetailsPaneResize(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement splitter)
        {
            splitter.ReleasePointerCaptures();
        }

        _detailsPaneResizeActive = false;
        e.Handled = true;
    }

    private void DetailsSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isDetailsPaneVisible)
        {
            return;
        }

        var step = IsModifierDown(VirtualKey.Shift)
            ? DetailsPaneKeyboardResizeLargeStep
            : DetailsPaneKeyboardResizeStep;

        switch (e.Key)
        {
            case VirtualKey.Left:
                ResizeDetailsPaneBy(step, announceStatus: true);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                ResizeDetailsPaneBy(-step, announceStatus: true);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                ResizeDetailsPaneTo(DetailsPaneMinWidth, announceStatus: true);
                e.Handled = true;
                break;
            case VirtualKey.End:
                var maxWidth = DetailsPaneColumn.MaxWidth > 0
                    ? DetailsPaneColumn.MaxWidth
                    : DefaultDetailsPaneWidth * 2;
                ResizeDetailsPaneTo(maxWidth, announceStatus: true);
                e.Handled = true;
                break;
        }
    }

    private void ResizeDetailsPaneBy(double delta, bool announceStatus = false)
    {
        if (!_isDetailsPaneVisible)
        {
            return;
        }

        var width = ReadDetailsPaneWidth();
        ResizeDetailsPaneTo(width + delta, announceStatus);
    }

    private void ResizeDetailsPaneTo(double width, bool announceStatus = false)
    {
        if (!_isDetailsPaneVisible)
        {
            return;
        }

        var clampedWidth = ClampDetailsPaneWidth(width);
        var currentWidth = ReadDetailsPaneWidth();
        if (Math.Abs(currentWidth - clampedWidth) < 0.5)
        {
            return;
        }

        _lastVisibleDetailsPaneWidth = clampedWidth;
        DetailsPaneColumn.Width = new GridLength(clampedWidth);
        ViewModel.SetCurrentDetailsPaneWidth(clampedWidth);

        if (announceStatus)
        {
            ViewModel.StatusLine = $"Details pane width: {Math.Round(clampedWidth)} px.";
        }
    }

    private ListViewBase GetActiveFileListControl()
    {
        var viewMode = ViewModel.SelectedTab?.ViewState.ViewMode ?? FolderViewMode.Details;

        return viewMode switch
        {
            FolderViewMode.Compact => CompactFileList,
            FolderViewMode.Icons => IconFileGrid,
            _ => FileList
        };
    }

    private async Task<string?> PromptForRenameAsync(string currentName)
    {
        var nameBox = new TextBox
        {
            Text = currentName,
            Width = 340
        };
        nameBox.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ApplyShellDialogChrome(dialog);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var newName = nameBox.Text.Trim();
        return string.IsNullOrWhiteSpace(newName) ? null : newName;
    }

    private static bool ContainsInvalidFileNameChars(string fileName) =>
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    private static DataPackageOperation ResolveDropOperation(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return DataPackageOperation.None;
        }

        return dataView.RequestedOperation == DataPackageOperation.Move
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;
    }

    private static async Task<List<string>> ExtractDroppedPathsAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var storageItems = await dataView.GetStorageItemsAsync();
        return storageItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<IStorageItem>> ResolveStorageItemsAsync(IEnumerable<string> sourcePaths)
    {
        var storageItems = new List<IStorageItem>();

        foreach (var sourcePath in sourcePaths)
        {
            try
            {
                if (Directory.Exists(sourcePath))
                {
                    storageItems.Add(await StorageFolder.GetFolderFromPathAsync(sourcePath));
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    storageItems.Add(await StorageFile.GetFileFromPathAsync(sourcePath));
                }
            }
            catch
            {
                // Why: Dropped shell items can disappear between drag start and resolution.
            }
        }

        return storageItems;
    }

    private static bool ShowNativePropertiesSheet(string path, nint ownerWindowHandle)
    {
        var executeInfo = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList | SeeMaskFlagNoUi,
            hwnd = ownerWindowHandle,
            lpVerb = "properties",
            lpFile = path,
            nShow = SwShow
        };

        return ShellExecuteEx(ref executeInfo);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    private static FileItem? ResolveFileItemFromOriginalSource(object? originalSource)
    {
        var current = originalSource as DependencyObject;

        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FileItem item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void RunUiAction(string actionName, Func<Task> action)
    {
        _ = RunUiActionAsync(actionName, action);
    }

    private async Task RunUiActionAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Why: User-initiated cancel paths are expected and should not surface as failures.
        }
        catch (Exception ex)
        {
            ViewModel.StatusLine = $"{actionName} failed: {ex.Message}";
        }
    }

    private bool IsSearchBoxFocused() => SearchBox.FocusState != FocusState.Unfocused;

    private static bool IsModifierDown(VirtualKey key)
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void ShowSyncToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        SyncToastText.Text = message;
        SyncToastHost.Visibility = Visibility.Visible;
        _syncToastTimer.Stop();
        _syncToastTimer.Start();
    }

    private void SyncToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _syncToastTimer.Stop();
        SyncToastHost.Visibility = Visibility.Collapsed;
    }

    private void AppSettings_Changed(object? sender, AppSettings settings)
    {
        DispatcherQueue.TryEnqueue(() => ApplyAppSettings(settings));
    }

    private void ApplyAppSettings(AppSettings settings)
    {
        RootGrid.RequestedTheme = settings.Appearance.ThemeMode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        Color? accent = TryParseAccentColor(settings.Appearance.AccentHex, out var parsed) ? parsed : null;
        if (accent is { } color && Application.Current?.Resources is { } resources)
        {
            ApplyAccentBrushes(resources, color);
            ApplyTreeViewAccentWashes(color);
        }

        _backdropController?.ApplyAccentTint(accent, RootGrid.ActualTheme);
        UpdateTitleBarLayout();
    }

    private static void ApplyAccentBrushes(ResourceDictionary resources, Color color)
    {
        var opaque = Color.FromArgb(0xFF, color.R, color.G, color.B);
        var mid = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R + 40, 0, 255),
            (byte)Math.Clamp(color.G + 30, 0, 255),
            (byte)Math.Clamp(color.B + 20, 0, 255));
        var light = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R + 90, 0, 255),
            (byte)Math.Clamp(color.G + 70, 0, 255),
            (byte)Math.Clamp(color.B + 40, 0, 255));
        var dark = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R - 30, 0, 255),
            (byte)Math.Clamp(color.G - 25, 0, 255),
            (byte)Math.Clamp(color.B - 20, 0, 255));

        resources["ShellAccentBrush"] = new SolidColorBrush(opaque);
        resources["FilesPlusPlusAccentBrush"] = new SolidColorBrush(opaque);
        resources["ShellAeroAccentGlowBorderBrush"] = new SolidColorBrush(opaque);
        resources["ShellTabSelectedBorderBrush"] = new SolidColorBrush(Color.FromArgb(0xA0, opaque.R, opaque.G, opaque.B));
        resources["ShellTabIndicatorBrush"] = new SolidColorBrush(opaque);
        resources["ShellSelectionBorderBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, opaque.R, opaque.G, opaque.B));
        resources["ShellListItemHoverBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x80, opaque.R, opaque.G, opaque.B));
        // Why: Caption hover stays soft glass-white; pressed picks up accent so min/max/close feel Aero, not neon.
        resources["ShellTitleBarButtonHoverBackgroundBrush"] = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        resources["ShellTitleBarButtonPressedBackgroundBrush"] = new SolidColorBrush(Color.FromArgb(0x70, opaque.R, opaque.G, opaque.B));
        resources["ShellSplitterHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, opaque.R, opaque.G, opaque.B));
        resources["ScrollBarThumbFillPointerOver"] = new SolidColorBrush(Color.FromArgb(0xC0, opaque.R, opaque.G, opaque.B));
        resources["ScrollBarThumbFillPressed"] = new SolidColorBrush(Color.FromArgb(0xE0, opaque.R, opaque.G, opaque.B));
        resources["ShellToastBorderBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, opaque.R, opaque.G, opaque.B));
        resources["ShellMenuFlyoutItemHoverBorderBrush"] = new SolidColorBrush(opaque);

        resources["ShellAeroAccentGlowBrush"] = CreateVerticalGradient(
            Color.FromArgb(0x70, mid.R, mid.G, mid.B),
            Color.FromArgb(0x40, opaque.R, opaque.G, opaque.B));
        resources["ShellSelectionBackgroundBrush"] = CreateVerticalGradient(
            Color.FromArgb(0xA0, mid.R, mid.G, mid.B),
            Color.FromArgb(0x70, opaque.R, opaque.G, opaque.B),
            Color.FromArgb(0x58, dark.R, dark.G, dark.B));
        resources["ShellCommandPrimaryButtonBackgroundBrush"] = CreateVerticalGradient(
            light, mid, opaque, dark);
        resources["ShellListItemHoverBackgroundBrush"] = CreateVerticalGradient(
            Color.FromArgb(0x78, light.R, light.G, light.B),
            Color.FromArgb(0x48, mid.R, mid.G, mid.B));
    }

    private void ApplyTreeViewAccentWashes(Color color)
    {
        if (NavigationTreeView?.Resources is not { } treeResources)
        {
            return;
        }

        var opaque = Color.FromArgb(0xFF, color.R, color.G, color.B);
        var mid = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R + 40, 0, 255),
            (byte)Math.Clamp(color.G + 30, 0, 255),
            (byte)Math.Clamp(color.B + 20, 0, 255));
        var dark = Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R - 30, 0, 255),
            (byte)Math.Clamp(color.G - 25, 0, 255),
            (byte)Math.Clamp(color.B - 20, 0, 255));

        treeResources["TreeViewItemBackgroundSelected"] = new SolidColorBrush(Color.FromArgb(0x58, opaque.R, opaque.G, opaque.B));
        treeResources["TreeViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(Color.FromArgb(0x70, mid.R, mid.G, mid.B));
        treeResources["TreeViewItemBackgroundSelectedPressed"] = new SolidColorBrush(Color.FromArgb(0x80, dark.R, dark.G, dark.B));
        treeResources["TreeViewItemBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0x34, opaque.R, opaque.G, opaque.B));
        treeResources["TreeViewItemBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0x48, opaque.R, opaque.G, opaque.B));
    }

    private static LinearGradientBrush CreateVerticalGradient(params Color[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };

        if (stops.Length == 0)
        {
            return brush;
        }

        for (var i = 0; i < stops.Length; i++)
        {
            var offset = stops.Length == 1 ? 0 : (double)i / (stops.Length - 1);
            brush.GradientStops.Add(new GradientStop { Color = stops[i], Offset = offset });
        }

        return brush;
    }

    private static bool TryParseAccentColor(string? hex, out Color color)
    {
        color = Microsoft.UI.Colors.Transparent;
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

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
                _settingsWindow.Closed += SettingsWindow_Closed;
            }

            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open settings window: {ex}");
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.Closed -= SettingsWindow_Closed;
        _settingsWindow = null;
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
