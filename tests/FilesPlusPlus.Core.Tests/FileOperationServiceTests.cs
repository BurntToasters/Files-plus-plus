using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Services;

namespace FilesPlusPlus.Core.Tests;

public sealed class FileOperationServiceTests
{
    [Fact]
    public async Task CopyMoveRenameDelete_CompletesSuccessfully()
    {
        await using var operationService = new FileOperationService();
        using var testDirectory = new TemporaryDirectory();

        var sourceFile = Path.Combine(testDirectory.Path, "source.txt");
        var copiedFile = Path.Combine(testDirectory.Path, "copy.txt");
        var renamedFile = Path.Combine(testDirectory.Path, "renamed.txt");
        var movedFile = Path.Combine(testDirectory.Path, "moved.txt");

        await File.WriteAllTextAsync(sourceFile, "files-plus-plus");

        var copyResult = await EnqueueAndWaitAsync(operationService, new OperationRequest(
            FileOperationType.Copy,
            sourceFile,
            copiedFile,
            Overwrite: true));
        Assert.True(copyResult.Succeeded);
        Assert.True(File.Exists(copiedFile));

        var renameResult = await EnqueueAndWaitAsync(operationService, new OperationRequest(
            FileOperationType.Rename,
            copiedFile,
            renamedFile,
            Overwrite: false,
            UseRecycleBin: false));
        Assert.True(renameResult.Succeeded);
        Assert.True(File.Exists(renamedFile));

        var moveResult = await EnqueueAndWaitAsync(operationService, new OperationRequest(
            FileOperationType.Move,
            renamedFile,
            movedFile,
            Overwrite: false));
        Assert.True(moveResult.Succeeded);
        Assert.True(File.Exists(movedFile));

        var deleteResult = await EnqueueAndWaitAsync(operationService, new OperationRequest(
            FileOperationType.Delete,
            movedFile,
            DestinationPath: null,
            Overwrite: false,
            UseRecycleBin: false));
        Assert.True(deleteResult.Succeeded);
        Assert.False(File.Exists(movedFile));
    }

    private static async Task<OperationResult> EnqueueAndWaitAsync(FileOperationService service, OperationRequest request)
    {
        var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid operationId = Guid.Empty;

        EventHandler<OperationResult>? handler = null;
        handler = (_, result) =>
        {
            if (result.OperationId != operationId)
            {
                return;
            }

            service.OperationCompleted -= handler;
            completion.TrySetResult(result);
        };

        service.OperationCompleted += handler;
        operationId = service.Enqueue(request);

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
