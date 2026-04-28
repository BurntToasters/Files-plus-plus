namespace FilesPlusPlus.Core.Models;

public sealed record FileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset DateModified,
    string TypeDisplay)
{
    public string SizeDisplay => IsDirectory || !SizeBytes.HasValue
        ? string.Empty
        : FormatBytes(SizeBytes.Value);

    public string ModifiedDisplay => DateModified.ToLocalTime().ToString("g");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
