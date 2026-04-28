namespace FilesPlusPlus.Core.Models;

public sealed record TabState(
    string CurrentPath,
    FolderViewState ViewState,
    List<string> BackHistory,
    List<string> ForwardHistory)
{
    public static TabState CreateDefault(string path) =>
        new(path, FolderViewState.Default, new List<string>(), new List<string>());
}
