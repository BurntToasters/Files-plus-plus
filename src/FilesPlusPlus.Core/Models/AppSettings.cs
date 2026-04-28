namespace FilesPlusPlus.Core.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public enum AppDensity
{
    Compact,
    Default,
    Spacious
}

public enum AppStartupBehavior
{
    RestoreTabs,
    HomeFolder,
    SpecificPath
}

public enum AppOpenItemMode
{
    DoubleClick,
    SingleClick
}

public sealed record AppearanceSettings(
    AppThemeMode ThemeMode,
    string AccentHex,
    AppDensity Density)
{
    public static AppearanceSettings Default { get; } =
        new(AppThemeMode.System, "#FF3A6EA5", AppDensity.Default);
}

public sealed record ViewSettings(
    FolderViewMode DefaultViewMode,
    SortColumn DefaultSortColumn,
    bool DefaultSortDescending,
    bool ShowHiddenFiles,
    bool ShowFileExtensions,
    bool GroupByDirectory)
{
    public static ViewSettings Default { get; } =
        new(FolderViewMode.Details, SortColumn.Name, DefaultSortDescending: false,
            ShowHiddenFiles: false, ShowFileExtensions: true, GroupByDirectory: true);
}

public sealed record NavigationSettings(
    AppStartupBehavior StartupBehavior,
    string? StartupPath,
    AppOpenItemMode OpenItemMode,
    bool ConfirmDelete)
{
    public static NavigationSettings Default { get; } =
        new(AppStartupBehavior.RestoreTabs, StartupPath: null, AppOpenItemMode.DoubleClick, ConfirmDelete: true);
}

public sealed record SidebarSettings(
    IReadOnlyList<string> PinnedPaths,
    bool ShowDrives)
{
    public static SidebarSettings Default { get; } =
        new(Array.Empty<string>(), ShowDrives: true);
}

public sealed record AppSettings(
    int SchemaVersion,
    AppearanceSettings Appearance,
    ViewSettings View,
    NavigationSettings Navigation,
    SidebarSettings Sidebar)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings CreateDefault() =>
        new(
            CurrentSchemaVersion,
            AppearanceSettings.Default,
            ViewSettings.Default,
            NavigationSettings.Default,
            SidebarSettings.Default);
}
