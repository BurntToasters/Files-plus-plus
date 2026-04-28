using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Utilities;

public static class PathUtilities
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or whitespace.", nameof(path));
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;

        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    public static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumb(string path)
    {
        var normalized = NormalizePath(path);
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var segments = new List<BreadcrumbSegment>();

        if (string.IsNullOrEmpty(root))
        {
            return segments;
        }

        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (current.Length == 2 && current[1] == ':')
        {
            current += Path.DirectorySeparatorChar;
        }

        segments.Add(new BreadcrumbSegment(root, current));

        var remaining = normalized[root.Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(remaining))
        {
            return segments;
        }

        foreach (var part in remaining.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add(new BreadcrumbSegment(part, current));
        }

        return segments;
    }
}
