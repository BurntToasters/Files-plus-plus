using System.Data.OleDb;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.Core.Services;

public sealed class SearchService : ISearchService
{
    private readonly IFileSystemService _fileSystemService;
    private readonly bool _preferWindowsIndex;

    public SearchService(IFileSystemService fileSystemService, bool preferWindowsIndex = true)
    {
        _fileSystemService = fileSystemService;
        _preferWindowsIndex = preferWindowsIndex;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ScopePath) || string.IsNullOrWhiteSpace(query.SearchText))
        {
            yield break;
        }

        var hasIndexResults = false;
        if (_preferWindowsIndex && OperatingSystem.IsWindows())
        {
            await foreach (var indexResult in SearchWindowsIndexAsync(query, cancellationToken).ConfigureAwait(false))
            {
                hasIndexResults = true;
                yield return indexResult;
            }
        }

        if (hasIndexResults)
        {
            yield break;
        }

        await foreach (var fallbackResult in SearchFileSystemFallbackAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return fallbackResult;
        }
    }

    [SupportedOSPlatform("windows")]
    private async IAsyncEnumerable<SearchResult> SearchWindowsIndexAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scopePath = PathUtilities.NormalizePath(query.ScopePath);
        var escapedScope = EscapeSqlLiteral(scopePath);
        var escapedSearch = EscapeContainsTerm(query.SearchText);
        var commandText =
            $"SELECT TOP {Math.Max(1, query.MaxResults)} System.ItemPathDisplay, System.FileName, System.Size, System.DateModified, System.FileAttributes " +
            $"FROM SYSTEMINDEX WHERE scope='file:{escapedScope}' AND CONTAINS(System.FileName,'\"{escapedSearch}*\"')";

        OleDbDataReader? reader;

#pragma warning disable CA1416
        using var connection =
            new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        using var command = new OleDbCommand(commandText, connection);
#pragma warning restore CA1416

        try
        {
            await Task.Run(connection.Open, cancellationToken).ConfigureAwait(false);
            reader = await Task.Run(command.ExecuteReader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            yield break;
        }

        if (reader is null)
        {
            yield break;
        }

        using (reader)
        {
            while (await Task.Run(reader.Read, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = reader["System.ItemPathDisplay"]?.ToString();
                var name = reader["System.FileName"]?.ToString();
                if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var fileAttributesRaw = reader["System.FileAttributes"];
                var isDirectory = fileAttributesRaw is not DBNull &&
                                  (((int)fileAttributesRaw & (int)FileAttributes.Directory) != 0);

                long? size = reader["System.Size"] is DBNull ? null : Convert.ToInt64(reader["System.Size"]);
                DateTimeOffset? modified = reader["System.DateModified"] is DBNull
                    ? null
                    : DateTime.SpecifyKind(Convert.ToDateTime(reader["System.DateModified"]), DateTimeKind.Utc);

                yield return new SearchResult(
                    FullPath: fullPath,
                    Name: name,
                    IsDirectory: isDirectory,
                    DateModified: modified,
                    SizeBytes: size);
            }
        }
    }

    private async IAsyncEnumerable<SearchResult> SearchFileSystemFallbackAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scopePath = _fileSystemService.NormalizePath(query.ScopePath);
        if (!Directory.Exists(scopePath))
        {
            yield break;
        }

        var normalizedSearch = query.SearchText.Trim();
        var resultsYielded = 0;

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(scopePath);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", options);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry);
                var isDirectory = Directory.Exists(entry);

                if (isDirectory)
                {
                    pendingDirectories.Push(entry);
                }

                if (!name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stat = await _fileSystemService.StatAsync(entry, cancellationToken).ConfigureAwait(false);
                if (stat is null)
                {
                    continue;
                }

                yield return new SearchResult(
                    FullPath: stat.FullPath,
                    Name: stat.Name,
                    IsDirectory: stat.IsDirectory,
                    DateModified: stat.DateModified,
                    SizeBytes: stat.SizeBytes);

                resultsYielded++;
                if (resultsYielded >= query.MaxResults)
                {
                    yield break;
                }
            }
        }
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string EscapeContainsTerm(string value) =>
        value.Replace("'", "''").Replace("\"", "\"\"");
}
