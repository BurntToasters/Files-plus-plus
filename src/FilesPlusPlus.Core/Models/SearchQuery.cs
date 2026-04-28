namespace FilesPlusPlus.Core.Models;

public sealed record SearchQuery(
    string ScopePath,
    string SearchText,
    int MaxResults = 200);
