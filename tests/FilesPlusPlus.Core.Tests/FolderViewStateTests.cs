using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.Core.Tests;

public sealed class FolderViewStateTests
{
    [Fact]
    public void ToggleSort_TogglesDirection_WhenSameColumn()
    {
        var state = FolderViewState.Default;
        var updated = state.ToggleSort(SortColumn.Name);

        Assert.Equal(SortColumn.Name, updated.SortColumn);
        Assert.Equal(SortDirection.Descending, updated.SortDirection);
    }

    [Fact]
    public void ToggleSort_ResetsToAscending_WhenNewColumn()
    {
        var state = FolderViewState.Default.ToggleSort(SortColumn.Name);
        var updated = state.ToggleSort(SortColumn.DateModified);

        Assert.Equal(SortColumn.DateModified, updated.SortColumn);
        Assert.Equal(SortDirection.Ascending, updated.SortDirection);
    }

    [Fact]
    public void FileItemSorter_PrioritizesDirectories_WhenGroupingEnabled()
    {
        var items = new[]
        {
            new FileItem("z.txt", @"C:\temp\z.txt", false, 10, DateTimeOffset.UtcNow, "TXT File"),
            new FileItem("folder", @"C:\temp\folder", true, null, DateTimeOffset.UtcNow, "File Folder"),
            new FileItem("a.txt", @"C:\temp\a.txt", false, 10, DateTimeOffset.UtcNow, "TXT File")
        };

        var sorted = FileItemSorter.Sort(items, FolderViewState.Default);

        Assert.True(sorted[0].IsDirectory);
        Assert.Equal("a.txt", sorted[1].Name);
        Assert.Equal("z.txt", sorted[2].Name);
    }

    [Fact]
    public void PaneStateHelpers_UpdateVisibilityAndWidth()
    {
        var state = FolderViewState.Default
            .WithDetailsPaneVisibility(false)
            .WithDetailsPaneWidth(480);

        Assert.False(state.IsDetailsPaneVisible);
        Assert.Equal(480, state.DetailsPaneWidth);
    }
}
