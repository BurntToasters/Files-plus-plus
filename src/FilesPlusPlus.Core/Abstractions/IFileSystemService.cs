using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Abstractions;

public interface IFileSystemService
{
    IAsyncEnumerable<FileItem> EnumerateItemsAsync(string folderPath, CancellationToken cancellationToken = default);

    IAsyncEnumerable<FileItem> EnumerateItemsAsync(
        string folderPath,
        FileEnumerationOptions options,
        CancellationToken cancellationToken = default);

    Task<FileItem?> StatAsync(string path, CancellationToken cancellationToken = default);

    string NormalizePath(string path);

    IDisposable Watch(
        string folderPath,
        Action<FileSystemEventArgs> onChanged,
        Action<RenamedEventArgs>? onRenamed = null);
}

public sealed record FileEnumerationOptions(bool ShowHidden = false, bool ShowFileExtensions = true)
{
    public static FileEnumerationOptions Default { get; } = new();
}
