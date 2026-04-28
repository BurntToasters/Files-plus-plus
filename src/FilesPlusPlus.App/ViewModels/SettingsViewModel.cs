using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilesPlusPlus.App.Models;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsService _settingsService;
    private readonly MainWindowViewModel _mainViewModel;

    public SettingsViewModel(IAppSettingsService settingsService, MainWindowViewModel mainViewModel)
    {
        _settingsService = settingsService;
        _mainViewModel = mainViewModel;

        var current = _settingsService.Current;
        _themeMode = current.Appearance.ThemeMode;
        _accentHex = current.Appearance.AccentHex;
        _density = current.Appearance.Density;

        _defaultViewMode = current.View.DefaultViewMode;
        _defaultSortColumn = current.View.DefaultSortColumn;
        _defaultSortDescending = current.View.DefaultSortDescending;
        _showHiddenFiles = current.View.ShowHiddenFiles;
        _showFileExtensions = current.View.ShowFileExtensions;
        _groupByDirectory = current.View.GroupByDirectory;

        _startupBehavior = current.Navigation.StartupBehavior;
        _startupPath = current.Navigation.StartupPath;
        _openItemMode = current.Navigation.OpenItemMode;
        _confirmDelete = current.Navigation.ConfirmDelete;

        _showDrives = current.Sidebar.ShowDrives;

        SidebarPins = new ObservableCollection<SidebarLocation>(_mainViewModel.SidebarLocations);

        Shortcuts = new ObservableCollection<ShortcutEntry>(BuildShortcutEntries());
    }

    public ObservableCollection<SidebarLocation> SidebarPins { get; }

    public ObservableCollection<ShortcutEntry> Shortcuts { get; }

    public IReadOnlyList<AppDensity> DensityOptions { get; } = Enum.GetValues<AppDensity>();

    public IReadOnlyList<FolderViewMode> ViewModeOptions { get; } = Enum.GetValues<FolderViewMode>();

    public IReadOnlyList<SortColumn> SortColumnOptions { get; } = Enum.GetValues<SortColumn>();

    public Func<Task<string?>>? PickFolderAsync { get; set; }

    [ObservableProperty]
    private AppThemeMode _themeMode;

    [ObservableProperty]
    private string _accentHex = "#FF3A6EA5";

    [ObservableProperty]
    private AppDensity _density;

    [ObservableProperty]
    private FolderViewMode _defaultViewMode;

    [ObservableProperty]
    private SortColumn _defaultSortColumn;

    [ObservableProperty]
    private bool _defaultSortDescending;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _showFileExtensions;

    [ObservableProperty]
    private bool _groupByDirectory;

    [ObservableProperty]
    private AppStartupBehavior _startupBehavior;

    [ObservableProperty]
    private string? _startupPath;

    [ObservableProperty]
    private AppOpenItemMode _openItemMode;

    [ObservableProperty]
    private bool _confirmDelete;

    [ObservableProperty]
    private bool _showDrives;

    [ObservableProperty]
    private SidebarLocation? _selectedSidebar;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsThemeSystem
    {
        get => ThemeMode == AppThemeMode.System;
        set
        {
            if (value)
            {
                ThemeMode = AppThemeMode.System;
            }
        }
    }

    public bool IsThemeLight
    {
        get => ThemeMode == AppThemeMode.Light;
        set
        {
            if (value)
            {
                ThemeMode = AppThemeMode.Light;
            }
        }
    }

    public bool IsThemeDark
    {
        get => ThemeMode == AppThemeMode.Dark;
        set
        {
            if (value)
            {
                ThemeMode = AppThemeMode.Dark;
            }
        }
    }

    public bool IsStartupRestore
    {
        get => StartupBehavior == AppStartupBehavior.RestoreTabs;
        set
        {
            if (value)
            {
                StartupBehavior = AppStartupBehavior.RestoreTabs;
            }
        }
    }

    public bool IsStartupHome
    {
        get => StartupBehavior == AppStartupBehavior.HomeFolder;
        set
        {
            if (value)
            {
                StartupBehavior = AppStartupBehavior.HomeFolder;
            }
        }
    }

    public bool IsStartupSpecific
    {
        get => StartupBehavior == AppStartupBehavior.SpecificPath;
        set
        {
            if (value)
            {
                StartupBehavior = AppStartupBehavior.SpecificPath;
            }
        }
    }

    public bool IsOpenSingleClick
    {
        get => OpenItemMode == AppOpenItemMode.SingleClick;
        set
        {
            if (value)
            {
                OpenItemMode = AppOpenItemMode.SingleClick;
            }
        }
    }

    public bool IsOpenDoubleClick
    {
        get => OpenItemMode == AppOpenItemMode.DoubleClick;
        set
        {
            if (value)
            {
                OpenItemMode = AppOpenItemMode.DoubleClick;
            }
        }
    }

    partial void OnThemeModeChanged(AppThemeMode value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    partial void OnStartupBehaviorChanged(AppStartupBehavior value)
    {
        OnPropertyChanged(nameof(IsStartupRestore));
        OnPropertyChanged(nameof(IsStartupHome));
        OnPropertyChanged(nameof(IsStartupSpecific));
    }

    partial void OnOpenItemModeChanged(AppOpenItemMode value)
    {
        OnPropertyChanged(nameof(IsOpenSingleClick));
        OnPropertyChanged(nameof(IsOpenDoubleClick));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = BuildSettingsSnapshot();

        await _settingsService.SaveAsync(settings);
        _mainViewModel.ApplySidebarFromSettings(SidebarPins);
        StatusMessage = "Settings saved.";
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var defaults = await _settingsService.ResetAsync();

        ThemeMode = defaults.Appearance.ThemeMode;
        AccentHex = defaults.Appearance.AccentHex;
        Density = defaults.Appearance.Density;

        DefaultViewMode = defaults.View.DefaultViewMode;
        DefaultSortColumn = defaults.View.DefaultSortColumn;
        DefaultSortDescending = defaults.View.DefaultSortDescending;
        ShowHiddenFiles = defaults.View.ShowHiddenFiles;
        ShowFileExtensions = defaults.View.ShowFileExtensions;
        GroupByDirectory = defaults.View.GroupByDirectory;

        StartupBehavior = defaults.Navigation.StartupBehavior;
        StartupPath = defaults.Navigation.StartupPath;
        OpenItemMode = defaults.Navigation.OpenItemMode;
        ConfirmDelete = defaults.Navigation.ConfirmDelete;

        ShowDrives = defaults.Sidebar.ShowDrives;
        SidebarPins.Clear();
        _mainViewModel.ApplySidebarFromSettings(Array.Empty<SidebarLocation>());
        SelectedSidebar = null;

        StatusMessage = "Settings reset to defaults.";
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedSidebar is null)
        {
            return;
        }

        var index = SidebarPins.IndexOf(SelectedSidebar);
        if (index > 0)
        {
            SidebarPins.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedSidebar is null)
        {
            return;
        }

        var index = SidebarPins.IndexOf(SelectedSidebar);
        if (index >= 0 && index < SidebarPins.Count - 1)
        {
            SidebarPins.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedSidebar is null)
        {
            return;
        }

        SidebarPins.Remove(SelectedSidebar);
    }

    [RelayCommand]
    private async Task AddPinAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        if (SidebarPins.Any(pin => string.Equals(pin.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SidebarPins.Add(new SidebarLocation(Path.GetFileName(path), path, "\uE838"));
    }

    [RelayCommand]
    private async Task PickStartupPathAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            StartupPath = path;
            StartupBehavior = AppStartupBehavior.SpecificPath;
        }
    }

    private static string? SanitizeStartupPath(string? startupPath)
    {
        if (string.IsNullOrWhiteSpace(startupPath))
        {
            return null;
        }

        return Directory.Exists(startupPath) ? startupPath : null;
    }

    public AppSettings BuildSettingsSnapshot()
    {
        var sanitizedStartupPath = StartupBehavior == AppStartupBehavior.SpecificPath
            ? SanitizeStartupPath(StartupPath)
            : null;

        return new AppSettings(
            AppSettings.CurrentSchemaVersion,
            new AppearanceSettings(ThemeMode, AccentHex, Density),
            new ViewSettings(DefaultViewMode, DefaultSortColumn, DefaultSortDescending, ShowHiddenFiles, ShowFileExtensions, GroupByDirectory),
            new NavigationSettings(StartupBehavior, sanitizedStartupPath, OpenItemMode, ConfirmDelete),
            new SidebarSettings(SidebarPins.Select(pin => pin.Path).ToList(), ShowDrives));
    }

    public bool HasUnsavedChanges() => !EqualityComparer<AppSettings>.Default.Equals(BuildSettingsSnapshot(), _settingsService.Current);

    private static IEnumerable<ShortcutEntry> BuildShortcutEntries() => new[]
    {
        new ShortcutEntry("New tab", "Ctrl+T"),
        new ShortcutEntry("Close tab", "Ctrl+W"),
        new ShortcutEntry("Back", "Alt+Left"),
        new ShortcutEntry("Forward", "Alt+Right"),
        new ShortcutEntry("Up one folder", "Alt+Up"),
        new ShortcutEntry("Refresh", "F5"),
        new ShortcutEntry("Focus search", "Ctrl+F"),
        new ShortcutEntry("Copy selection", "Ctrl+C"),
        new ShortcutEntry("Cut selection", "Ctrl+X"),
        new ShortcutEntry("Paste", "Ctrl+V"),
        new ShortcutEntry("Delete", "Delete"),
        new ShortcutEntry("Rename", "F2"),
        new ShortcutEntry("New folder", "Ctrl+Shift+N"),
        new ShortcutEntry("Toggle details pane", "Ctrl+Shift+P"),
        new ShortcutEntry("Focus details splitter", "Ctrl+Shift+D"),
        new ShortcutEntry("Show shortcuts", "F1"),
    };
}

public sealed record ShortcutEntry(string Action, string Keys);
