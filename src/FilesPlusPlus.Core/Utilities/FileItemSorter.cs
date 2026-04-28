using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Utilities;

public static class FileItemSorter
{
    public static IReadOnlyList<FileItem> Sort(IEnumerable<FileItem> source, FolderViewState viewState)
    {
        IOrderedEnumerable<FileItem> ordered = viewState.GroupDirectoriesFirst
            ? source.OrderByDescending(item => item.IsDirectory)
            : source.OrderBy(item => 0);

        var sorted = viewState.SortColumn switch
        {
            SortColumn.Type => viewState.SortDirection == SortDirection.Ascending
                ? ordered.ThenBy(item => item.TypeDisplay, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(item => item.TypeDisplay, StringComparer.OrdinalIgnoreCase),
            SortColumn.DateModified => viewState.SortDirection == SortDirection.Ascending
                ? ordered.ThenBy(item => item.DateModified)
                : ordered.ThenByDescending(item => item.DateModified),
            SortColumn.Size => viewState.SortDirection == SortDirection.Ascending
                ? ordered.ThenBy(item => item.SizeBytes ?? -1)
                : ordered.ThenByDescending(item => item.SizeBytes ?? -1),
            _ => viewState.SortDirection == SortDirection.Ascending
                ? ordered.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };

        return sorted.ToList();
    }
}
