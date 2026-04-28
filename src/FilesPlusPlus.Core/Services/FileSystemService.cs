using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.Core.Services;

public sealed class FileSystemService : IFileSystemService
{
    public IAsyncEnumerable<FileItem> EnumerateItemsAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
        => EnumerateItemsAsync(folderPath, FileEnumerationOptions.Default, cancellationToken);

    public async IAsyncEnumerable<FileItem> EnumerateItemsAsync(
        string folderPath,
        FileEnumerationOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedPath = NormalizePath(folderPath);
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.ShowHidden
                ? FileAttributes.None
                : FileAttributes.Hidden | FileAttributes.System
        };

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(normalizedPath, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await StatAsync(entryPath, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                continue;
            }

            if (!options.ShowFileExtensions && !item.IsDirectory)
            {
                var withoutExtension = Path.GetFileNameWithoutExtension(item.Name);
                if (!string.IsNullOrEmpty(withoutExtension) && !string.Equals(withoutExtension, item.Name, StringComparison.Ordinal))
                {
                    item = item with { Name = withoutExtension };
                }
            }

            yield return item;
        }
    }

    public Task<FileItem?> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = NormalizePath(path);

        if (Directory.Exists(normalizedPath))
        {
            var info = new DirectoryInfo(normalizedPath);
            return Task.FromResult<FileItem?>(new FileItem(
                Name: info.Name,
                FullPath: info.FullName,
                IsDirectory: true,
                SizeBytes: null,
                DateModified: info.LastWriteTimeUtc,
                TypeDisplay: "File Folder"));
        }

        if (!File.Exists(normalizedPath))
        {
            return Task.FromResult<FileItem?>(null);
        }

        var fileInfo = new FileInfo(normalizedPath);
        var extension = Path.GetExtension(fileInfo.Name);
        var typeDisplay = string.IsNullOrWhiteSpace(extension)
            ? "File"
            : $"{extension.TrimStart('.').ToUpperInvariant()} File";

        return Task.FromResult<FileItem?>(new FileItem(
            Name: fileInfo.Name,
            FullPath: fileInfo.FullName,
            IsDirectory: false,
            SizeBytes: fileInfo.Length,
            DateModified: fileInfo.LastWriteTimeUtc,
            TypeDisplay: typeDisplay));
    }

    public string NormalizePath(string path) => PathUtilities.NormalizePath(path);

    public IDisposable Watch(
        string folderPath,
        Action<FileSystemEventArgs> onChanged,
        Action<RenamedEventArgs>? onRenamed = null)
    {
        var normalizedPath = NormalizePath(folderPath);

        var watcher = new FileSystemWatcher(normalizedPath)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
        };

        watcher.Changed += (_, args) => onChanged(args);
        watcher.Created += (_, args) => onChanged(args);
        watcher.Deleted += (_, args) => onChanged(args);
        watcher.Renamed += (_, args) =>
        {
            onChanged(args);
            onRenamed?.Invoke(args);
        };

        return watcher;
    }
}
