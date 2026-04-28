using System.Text.RegularExpressions;

namespace FilesPlusPlus.Core.Tests;

public sealed class CommentPolicyTests
{
    private static readonly HashSet<string> ScopedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".xaml",
        ".ps1"
    };

    private static readonly string[] ScopedRoots =
    {
        "src",
        "tests",
        "scripts"
    };

    [Fact]
    public void RepositoryComments_MatchPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new List<string>();

        foreach (var filePath in EnumerateScopedFiles(repositoryRoot))
        {
            var content = File.ReadAllText(filePath);
            var relativePath = Path.GetRelativePath(repositoryRoot, filePath);
            violations.AddRange(ValidateComments(relativePath, content));
        }

        Assert.True(
            violations.Count == 0,
            "Comment policy violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void WhyComment_PassesPolicy()
    {
        var violations = ValidateComments("sample.cs", "var value = 1;" + Environment.NewLine + "// Why: Value 1 preserves protocol compatibility.");
        Assert.Empty(violations);
    }

    [Fact]
    public void SectionComment_PassesPolicy()
    {
        var violations = ValidateComments("sample.xaml", "<Grid />" + Environment.NewLine + "<!-- Section: Main workspace -->");
        Assert.Empty(violations);
    }

    [Fact]
    public void FreeFormComment_FailsPolicy()
    {
        var violations = ValidateComments("sample.cs", "var ready = true;" + Environment.NewLine + "// Ignore cancellation.");
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void XmlDocumentationComment_FailsPolicy()
    {
        var violations = ValidateComments("sample.cs", "/// <summary>Info</summary>" + Environment.NewLine + "public sealed class Sample { }");
        Assert.NotEmpty(violations);
    }

    private static IEnumerable<string> EnumerateScopedFiles(string repositoryRoot)
    {
        foreach (var scopedRoot in ScopedRoots)
        {
            var rootPath = Path.Combine(repositoryRoot, scopedRoot);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(repositoryRoot, filePath))
                {
                    continue;
                }

                var extension = Path.GetExtension(filePath);
                if (ScopedExtensions.Contains(extension))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static bool IsExcludedPath(string repositoryRoot, string filePath)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, filePath);
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ValidateComments(string relativePath, string content)
    {
        var extension = Path.GetExtension(relativePath);
        var comments = extension.ToLowerInvariant() switch
        {
            ".cs" => ExtractCSharpComments(content),
            ".xaml" => ExtractXmlComments(content),
            ".ps1" => ExtractPowerShellComments(content),
            _ => []
        };

        var violations = new List<string>();
        foreach (var comment in comments)
        {
            var message = ValidateCommentText(comment.Text);
            if (message is null)
            {
                continue;
            }

            violations.Add($"{relativePath}:{comment.Line} {message} ({comment.Text.Trim()})");
        }

        return violations;
    }

    private static string? ValidateCommentText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return "Comment text is empty.";
        }

        if (!trimmed.StartsWith("Why:", StringComparison.Ordinal) &&
            !trimmed.StartsWith("Section:", StringComparison.Ordinal))
        {
            return "Comment must start with Why: or Section:.";
        }

        var payload = trimmed.StartsWith("Why:", StringComparison.Ordinal)
            ? trimmed["Why:".Length..].Trim()
            : trimmed["Section:".Length..].Trim();

        if (payload.Length == 0)
        {
            return "Comment payload cannot be empty.";
        }

        if (Regex.IsMatch(payload, "^[=\\-–—_~`*./\\\\|+\\s]+$"))
        {
            return "Decorative separator comments are not allowed.";
        }

        return null;
    }

    private static List<CommentOccurrence> ExtractPowerShellComments(string content)
    {
        var matches = Regex.Matches(content, "^[ \\t]*#(?<text>.*)$", RegexOptions.Multiline);
        var results = new List<CommentOccurrence>(matches.Count);
        foreach (Match match in matches)
        {
            var line = CountLines(content, match.Index);
            var text = match.Groups["text"].Value;
            results.Add(new CommentOccurrence(line, text));
        }

        return results;
    }

    private static List<CommentOccurrence> ExtractXmlComments(string content)
    {
        var matches = Regex.Matches(content, "<!--(?<text>.*?)-->", RegexOptions.Singleline);
        var results = new List<CommentOccurrence>(matches.Count);
        foreach (Match match in matches)
        {
            var line = CountLines(content, match.Index);
            var text = match.Groups["text"].Value;
            results.Add(new CommentOccurrence(line, text));
        }

        return results;
    }

    private static List<CommentOccurrence> ExtractCSharpComments(string content)
    {
        var results = new List<CommentOccurrence>();
        var length = content.Length;
        var line = 1;
        var i = 0;

        while (i < length)
        {
            var current = content[i];

            if (current == '\r')
            {
                i++;
                continue;
            }

            if (current == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (current == '$')
            {
                if (i + 2 < length && content[i + 1] == '@' && content[i + 2] == '"')
                {
                    i += 3;
                    ConsumeVerbatimString(content, ref i, ref line);
                    continue;
                }

                if (i + 1 < length && content[i + 1] == '"')
                {
                    i += 2;
                    ConsumeStandardString(content, ref i, ref line);
                    continue;
                }
            }

            if (current == '@' && i + 1 < length && content[i + 1] == '"')
            {
                i += 2;
                ConsumeVerbatimString(content, ref i, ref line);
                continue;
            }

            if (current == '"' && i + 2 < length && content[i + 1] == '"' && content[i + 2] == '"')
            {
                var delimiterLength = 3;
                while (i + delimiterLength < length && content[i + delimiterLength] == '"')
                {
                    delimiterLength++;
                }

                i += delimiterLength;
                ConsumeRawString(content, ref i, ref line, delimiterLength);
                continue;
            }

            if (current == '"')
            {
                i++;
                ConsumeStandardString(content, ref i, ref line);
                continue;
            }

            if (current == '\'')
            {
                i++;
                ConsumeCharacterLiteral(content, ref i, ref line);
                continue;
            }

            if (current == '/' && i + 1 < length)
            {
                var next = content[i + 1];
                if (next == '/')
                {
                    var commentLine = line;
                    i += 2;
                    var start = i;
                    while (i < length && content[i] != '\n')
                    {
                        i++;
                    }

                    var text = content[start..i];
                    results.Add(new CommentOccurrence(commentLine, text));
                    continue;
                }

                if (next == '*')
                {
                    var commentLine = line;
                    i += 2;
                    var start = i;

                    while (i < length)
                    {
                        if (content[i] == '\n')
                        {
                            line++;
                        }

                        if (content[i] == '*' && i + 1 < length && content[i + 1] == '/')
                        {
                            var text = content[start..i];
                            results.Add(new CommentOccurrence(commentLine, text));
                            i += 2;
                            break;
                        }

                        i++;
                    }

                    continue;
                }
            }

            i++;
        }

        return results;
    }

    private static void ConsumeStandardString(string content, ref int index, ref int line)
    {
        while (index < content.Length)
        {
            var current = content[index];

            if (current == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (current == '\\')
            {
                index += Math.Min(2, content.Length - index);
                continue;
            }

            if (current == '"')
            {
                index++;
                break;
            }

            index++;
        }
    }

    private static void ConsumeVerbatimString(string content, ref int index, ref int line)
    {
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (current == '"' && index + 1 < content.Length && content[index + 1] == '"')
            {
                index += 2;
                continue;
            }

            if (current == '"')
            {
                index++;
                break;
            }

            index++;
        }
    }

    private static void ConsumeRawString(string content, ref int index, ref int line, int delimiterLength)
    {
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (current == '"')
            {
                var run = 1;
                while (index + run < content.Length && content[index + run] == '"')
                {
                    run++;
                }

                if (run >= delimiterLength)
                {
                    index += run;
                    break;
                }

                index += run;
                continue;
            }

            index++;
        }
    }

    private static void ConsumeCharacterLiteral(string content, ref int index, ref int line)
    {
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '\n')
            {
                line++;
                index++;
                break;
            }

            if (current == '\\')
            {
                index += Math.Min(2, content.Length - index);
                continue;
            }

            if (current == '\'')
            {
                index++;
                break;
            }

            index++;
        }
    }

    private static int CountLines(string text, int upToIndex)
    {
        var line = 1;
        for (var i = 0; i < upToIndex; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            var hasSolution = File.Exists(Path.Combine(candidate.FullName, "FilesPlusPlus.sln"));
            var hasSource = Directory.Exists(Path.Combine(candidate.FullName, "src"));
            var hasTests = Directory.Exists(Path.Combine(candidate.FullName, "tests"));
            var hasScripts = Directory.Exists(Path.Combine(candidate.FullName, "scripts"));
            if (hasSolution && hasSource && hasTests && hasScripts)
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException("Repository root not found for comment policy tests.");
    }

    private readonly record struct CommentOccurrence(int Line, string Text);
}
