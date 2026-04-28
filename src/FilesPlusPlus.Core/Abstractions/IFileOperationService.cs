using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Abstractions;

public interface IFileOperationService : IAsyncDisposable
{
    event EventHandler<OperationProgress>? ProgressChanged;

    event EventHandler<OperationResult>? OperationCompleted;

    Guid Enqueue(OperationRequest request);

    bool Cancel(Guid operationId);
}
