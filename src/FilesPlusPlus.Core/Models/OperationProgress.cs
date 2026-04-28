namespace FilesPlusPlus.Core.Models;

public sealed record OperationProgress(
    Guid OperationId,
    FileOperationType Type,
    int CompletedItems,
    int TotalItems,
    string CurrentPath,
    bool IsCompleted);

public sealed record OperationResult(
    Guid OperationId,
    FileOperationType Type,
    bool Succeeded,
    string? ErrorMessage = null);
