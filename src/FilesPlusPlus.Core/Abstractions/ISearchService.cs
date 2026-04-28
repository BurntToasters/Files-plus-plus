using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Abstractions;

public interface ISearchService
{
    IAsyncEnumerable<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}
