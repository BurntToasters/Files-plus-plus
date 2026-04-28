namespace FilesPlusPlus.Core.Models;

public sealed record WindowLayout(
    double Width,
    double Height,
    bool IsMaximized)
{
    public double DetailsPaneWidth { get; init; } = 320;
    public bool IsDetailsPaneVisible { get; init; } = true;
}

public sealed record SessionState(
    int SchemaVersion,
    List<TabState> Tabs,
    int SelectedTabIndex,
    WindowLayout WindowLayout,
    List<string> SidebarPins,
    DateTimeOffset SavedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static SessionState CreateDefault(string startupPath)
    {
        return new SessionState(
            CurrentSchemaVersion,
            new List<TabState> { TabState.CreateDefault(startupPath) },
            SelectedTabIndex: 0,
            new WindowLayout(1360, 860, IsMaximized: false)
            {
                DetailsPaneWidth = 320,
                IsDetailsPaneVisible = true
            },
            new List<string>(),
            DateTimeOffset.UtcNow);
    }
}
