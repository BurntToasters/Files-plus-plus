using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.Core.Tests;

public sealed class PathUtilitiesTests
{
    [Fact]
    public void NormalizePath_RemovesTrailingSeparators_ForNonRootPaths()
    {
        var normalized = PathUtilities.NormalizePath(@"C:\Temp\FilesPlusPlus\");
        Assert.False(normalized.EndsWith(@"\", StringComparison.Ordinal));
        Assert.Contains("FilesPlusPlus", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBreadcrumb_ReturnsCumulativeSegments()
    {
        var path = @"C:\Users\Burnt\Documents";
        var breadcrumbs = PathUtilities.BuildBreadcrumb(path);

        Assert.True(breadcrumbs.Count >= 3);
        Assert.Equal(@"C:\", breadcrumbs[0].Label);
        Assert.Equal("Users", breadcrumbs[1].Label);
        Assert.Equal(@"C:\Users", breadcrumbs[1].FullPath);
    }
}
