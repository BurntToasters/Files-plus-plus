namespace FilesPlusPlus.Core.Models;

public enum SortColumn
{
    Name,
    Type,
    DateModified,
    Size
}

public enum SortDirection
{
    Ascending,
    Descending
}

public enum FolderViewMode
{
    Details,
    Compact,
    Icons
}

public sealed record FolderViewState(
    SortColumn SortColumn,
    SortDirection SortDirection,
    bool GroupDirectoriesFirst,
    string? SearchText)
{
    public FolderViewMode ViewMode { get; init; } = FolderViewMode.Details;
    public bool IsDetailsPaneVisible { get; init; } = true;
    public double DetailsPaneWidth { get; init; } = 320;

    public static FolderViewState Default { get; } =
        new(SortColumn.Name, SortDirection.Ascending, GroupDirectoriesFirst: true, SearchText: null);

    public FolderViewState ToggleSort(SortColumn column)
    {
        if (SortColumn == column)
        {
            return this with
            {
                SortDirection = SortDirection == SortDirection.Ascending
                    ? SortDirection.Descending
                    : SortDirection.Ascending
            };
        }

        return this with
        {
            SortColumn = column,
            SortDirection = SortDirection.Ascending
        };
    }

    public FolderViewState WithSearch(string? searchText) =>
        this with { SearchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim() };

    public FolderViewState WithViewMode(FolderViewMode viewMode) =>
        this with { ViewMode = viewMode };

    public FolderViewState WithDetailsPaneVisibility(bool isVisible) =>
        this with { IsDetailsPaneVisible = isVisible };

    public FolderViewState WithDetailsPaneWidth(double width) =>
        this with { DetailsPaneWidth = width };
}
