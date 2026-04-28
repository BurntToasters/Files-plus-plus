namespace FilesPlusPlus.Core.Models;

public sealed record SearchResult(
    string FullPath,
    string Name,
    bool IsDirectory,
    DateTimeOffset? DateModified,
    long? SizeBytes);
