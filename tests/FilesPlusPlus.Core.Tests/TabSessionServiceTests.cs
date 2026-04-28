using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Services;

namespace FilesPlusPlus.Core.Tests;

public sealed class TabSessionServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSessionState()
    {
        using var testDirectory = new TemporaryDirectory();
        var sessionPath = Path.Combine(testDirectory.Path, "session-state.json");
        var service = new TabSessionService(sessionPath, defaultPath: testDirectory.Path);

        var original = new SessionState(
            SessionState.CurrentSchemaVersion,
            new List<TabState>
            {
                new(
                    testDirectory.Path,
                    FolderViewState.Default with
                    {
                        SearchText = "budget",
                        IsDetailsPaneVisible = false,
                        DetailsPaneWidth = 460
                    },
                    new List<string> { @"C:\Users" },
                    new List<string>())
            },
            SelectedTabIndex: 0,
            new WindowLayout(1440, 920, IsMaximized: false)
            {
                DetailsPaneWidth = 420,
                IsDetailsPaneVisible = false
            },
            new List<string> { testDirectory.Path },
            DateTimeOffset.UtcNow);

        await service.SaveAsync(original);
        var loaded = await service.LoadAsync();

        Assert.Equal(SessionState.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Single(loaded.Tabs);
        Assert.Equal("budget", loaded.Tabs[0].ViewState.SearchText);
        Assert.False(loaded.Tabs[0].ViewState.IsDetailsPaneVisible);
        Assert.Equal(460, loaded.Tabs[0].ViewState.DetailsPaneWidth);
        Assert.Equal(1440, loaded.WindowLayout.Width);
        Assert.Equal(920, loaded.WindowLayout.Height);
        Assert.Equal(420, loaded.WindowLayout.DetailsPaneWidth);
        Assert.False(loaded.WindowLayout.IsDetailsPaneVisible);
    }

    [Fact]
    public async Task Load_WhenDetailsPaneFieldsMissing_UsesDefaults()
    {
        using var testDirectory = new TemporaryDirectory();
        var sessionPath = Path.Combine(testDirectory.Path, "session-state.json");
        var service = new TabSessionService(sessionPath, defaultPath: testDirectory.Path);

        var legacyJson = """
                         {
                           "schemaVersion": 1,
                           "tabs": [
                             {
                               "currentPath": "C:\\Temp",
                               "viewState": {
                                 "sortColumn": 0,
                                 "sortDirection": 0,
                                 "groupDirectoriesFirst": true,
                                 "searchText": null,
                                 "viewMode": 0
                               },
                               "backHistory": [],
                               "forwardHistory": []
                             }
                           ],
                           "selectedTabIndex": 0,
                           "windowLayout": {
                             "width": 1200,
                             "height": 800,
                             "isMaximized": false
                           },
                           "sidebarPins": [],
                           "savedAt": "2026-04-25T00:00:00+00:00"
                         }
                         """;

        await File.WriteAllTextAsync(sessionPath, legacyJson);

        var loaded = await service.LoadAsync();

        Assert.True(loaded.Tabs[0].ViewState.IsDetailsPaneVisible);
        Assert.Equal(320, loaded.Tabs[0].ViewState.DetailsPaneWidth);
        Assert.Equal(320, loaded.WindowLayout.DetailsPaneWidth);
        Assert.True(loaded.WindowLayout.IsDetailsPaneVisible);
    }
}
