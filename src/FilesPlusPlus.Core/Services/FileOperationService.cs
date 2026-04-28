using System.Collections.Concurrent;
using System.Threading.Channels;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;
using Microsoft.VisualBasic.FileIO;

namespace FilesPlusPlus.Core.Services;

public sealed class FileOperationService : IFileOperationService
{
    private readonly Channel<(Guid OperationId, OperationRequest Request)> _queue =
        Channel.CreateUnbounded<(Guid, OperationRequest)>();

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operationTokens = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    public FileOperationService()
    {
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<OperationProgress>? ProgressChanged;

    public event EventHandler<OperationResult>? OperationCompleted;

    public Guid Enqueue(OperationRequest request)
    {
        ValidateRequest(request);
        var operationId = Guid.NewGuid();
        var operationToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        if (!_operationTokens.TryAdd(operationId, operationToken))
        {
            throw new InvalidOperationException("Unable to register operation token.");
        }

        if (!_queue.Writer.TryWrite((operationId, request)))
        {
            _operationTokens.TryRemove(operationId, out _);
            throw new InvalidOperationException("Operation queue is unavailable.");
        }

        return operationId;
    }

    public bool Cancel(Guid operationId)
    {
        if (!_operationTokens.TryGetValue(operationId, out var token))
        {
            return false;
        }

        token.Cancel();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Why: Worker cancellation is expected after signaling service shutdown.
        }

        foreach (var token in _operationTokens.Values)
        {
            token.Dispose();
        }

        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            if (!_operationTokens.TryGetValue(queued.OperationId, out var tokenSource))
            {
                continue;
            }

            try
            {
                await ExecuteOperationAsync(queued.OperationId, queued.Request, tokenSource.Token).ConfigureAwait(false);
                OperationCompleted?.Invoke(
                    this,
                    new OperationResult(queued.OperationId, queued.Request.Type, Succeeded: true));
            }
            catch (OperationCanceledException)
            {
                OperationCompleted?.Invoke(
                    this,
                    new OperationResult(queued.OperationId, queued.Request.Type, Succeeded: false, "Operation canceled."));
            }
            catch (Exception ex)
            {
                OperationCompleted?.Invoke(
                    this,
                    new OperationResult(queued.OperationId, queued.Request.Type, Succeeded: false, ex.Message));
            }
            finally
            {
                if (_operationTokens.TryRemove(queued.OperationId, out var removedToken))
                {
                    removedToken.Dispose();
                }
            }
        }
    }

    private async Task ExecuteOperationAsync(Guid operationId, OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RaiseProgress(operationId, request.Type, completedItems: 0, totalItems: 1, request.SourcePath, isCompleted: false);

        switch (request.Type)
        {
            case FileOperationType.Copy:
                await CopyAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case FileOperationType.Move:
                await MoveAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case FileOperationType.Delete:
                await DeleteAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case FileOperationType.Rename:
                await RenameAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Type), "Unsupported operation type.");
        }

        RaiseProgress(operationId, request.Type, completedItems: 1, totalItems: 1, request.SourcePath, isCompleted: true);
    }

    private static Task CopyAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = PathUtilities.NormalizePath(request.SourcePath);
        var destinationPath = PathUtilities.NormalizePath(request.DestinationPath ?? throw new InvalidOperationException("Copy destination is required."));
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: request.Overwrite);
            return Task.CompletedTask;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        if (IsDestinationWithinSourceDirectory(sourcePath, destinationPath))
        {
            throw new IOException("Cannot copy a folder into itself or its subfolder.");
        }

        CopyDirectory(sourcePath, destinationPath, request.Overwrite, cancellationToken);
        return Task.CompletedTask;
    }

    private static Task MoveAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = PathUtilities.NormalizePath(request.SourcePath);
        var destinationPath = PathUtilities.NormalizePath(request.DestinationPath ?? throw new InvalidOperationException("Move destination is required."));
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath, overwrite: request.Overwrite);
            return Task.CompletedTask;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        if (IsDestinationWithinSourceDirectory(sourcePath, destinationPath))
        {
            throw new IOException("Cannot move a folder into itself or its subfolder.");
        }

        if (Directory.Exists(destinationPath))
        {
            if (!request.Overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            Directory.Delete(destinationPath, recursive: true);
        }

        Directory.Move(sourcePath, destinationPath);
        return Task.CompletedTask;
    }

    private static Task RenameAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = PathUtilities.NormalizePath(request.SourcePath);
        var destinationPath = PathUtilities.NormalizePath(request.DestinationPath ?? throw new InvalidOperationException("Rename destination is required."));
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath, overwrite: request.Overwrite);
            return Task.CompletedTask;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        if (Directory.Exists(destinationPath))
        {
            if (!request.Overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            Directory.Delete(destinationPath, recursive: true);
        }

        Directory.Move(sourcePath, destinationPath);
        return Task.CompletedTask;
    }

    private static Task DeleteAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = PathUtilities.NormalizePath(request.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            if (request.UseRecycleBin)
            {
                FileSystem.DeleteFile(
                    sourcePath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }
            else
            {
                File.Delete(sourcePath);
            }

            return Task.CompletedTask;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        if (request.UseRecycleBin)
        {
            FileSystem.DeleteDirectory(
                sourcePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        else
        {
            Directory.Delete(sourcePath, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(destinationPath))
        {
            Directory.CreateDirectory(destinationPath);
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(destinationPath, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite);
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationDirectory = Path.Combine(destinationPath, Path.GetFileName(sourceDirectory));
            CopyDirectory(sourceDirectory, destinationDirectory, overwrite, cancellationToken);
        }
    }

    private static bool IsDestinationWithinSourceDirectory(string sourcePath, string destinationPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationFullPath = Path.GetFullPath(destinationPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return destinationFullPath.StartsWith(sourceFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(destinationFullPath, sourceFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRequest(OperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new ArgumentException("SourcePath is required.", nameof(request));
        }

        if (request.Type is FileOperationType.Copy or FileOperationType.Move or FileOperationType.Rename)
        {
            if (string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                throw new ArgumentException("DestinationPath is required for this operation type.", nameof(request));
            }
        }
    }

    private void RaiseProgress(
        Guid operationId,
        FileOperationType type,
        int completedItems,
        int totalItems,
        string currentPath,
        bool isCompleted)
    {
        ProgressChanged?.Invoke(
            this,
            new OperationProgress(operationId, type, completedItems, totalItems, currentPath, isCompleted));
    }
}
