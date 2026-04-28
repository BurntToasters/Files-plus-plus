namespace FilesPlusPlus.Core.Models;

public enum FileOperationType
{
    Copy,
    Move,
    Delete,
    Rename
}

public sealed record OperationRequest(
    FileOperationType Type,
    string SourcePath,
    string? DestinationPath = null,
    bool Overwrite = false,
    bool UseRecycleBin = true);
