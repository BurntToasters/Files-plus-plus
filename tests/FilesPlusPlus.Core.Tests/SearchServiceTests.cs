using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Services;

namespace FilesPlusPlus.Core.Tests;

public sealed class SearchServiceTests
{
    [Fact]
    public async Task SearchFallback_FindsMatchesInNestedDirectories()
    {
        using var testDirectory = new TemporaryDirectory();

        var nestedDirectory = Path.Combine(testDirectory.Path, "alpha", "beta");
        Directory.CreateDirectory(nestedDirectory);

        var targetPath = Path.Combine(nestedDirectory, "project-plan-notes.txt");
        await File.WriteAllTextAsync(targetPath, "notes");

        var fileSystemService = new FileSystemService();
        var searchService = new SearchService(fileSystemService, preferWindowsIndex: false);
        var query = new SearchQuery(testDirectory.Path, "plan", MaxResults: 25);

        var results = new List<SearchResult>();
        await foreach (var result in searchService.SearchAsync(query))
        {
            results.Add(result);
        }

        Assert.Contains(results, result => result.FullPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
    }
}
