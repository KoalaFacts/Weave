using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for connector testability.")]
internal sealed class FileSystemSearchOperations
{
    public ToolResult SearchFiles(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
            return Failure(toolName, "Parameter 'pattern' is required for search_files", sw);

        if (pattern.Contains("..", StringComparison.Ordinal))
            return Failure(toolName, "Pattern must not contain '..'", sw);

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(config.Root, pattern, SearchOption.AllDirectories))
        {
            var resolvedFile = Path.GetFullPath(file);
            try
            { FileSystemPathGuard.VerifyContainment(config.Root, resolvedFile); }
            catch
            { continue; }

            results.Add(Path.GetRelativePath(config.Root, resolvedFile));
            if (results.Count >= 1000)
                break;
        }

        return Success(toolName, string.Join("\n", results), sw);
    }

    public async Task<ToolResult> GrepAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
            return Failure(toolName, "Parameter 'pattern' is required for grep", sw);

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled;
            if (invocation.Parameters.TryGetValue("case_insensitive", out var caseInsensitive) &&
                caseInsensitive.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                options |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(pattern, options, matchTimeout: TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException ex)
        { return Failure(toolName, $"Invalid regex pattern: {ex.Message}", sw); }

        var glob = invocation.Parameters.GetValueOrDefault("glob", "*");
        if (glob.Contains("..", StringComparison.Ordinal))
            return Failure(toolName, "Glob must not contain '..'", sw);

        var output = await GrepFilesAsync(config, regex, glob, ct);
        return Success(toolName, output, sw);
    }

    private static async Task<string> GrepFilesAsync(FileSystemToolConfig config, Regex regex, string glob, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var matchCount = 0;
        const int maxMatches = 500;

        foreach (var file in Directory.EnumerateFiles(config.Root, glob, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (matchCount >= maxMatches)
                break;

            var resolvedFile = Path.GetFullPath(file);
            try
            { FileSystemPathGuard.VerifyContainment(config.Root, resolvedFile); }
            catch
            { continue; }

            if (!await FileSystemTextFile.CanReadAsTextAsync(resolvedFile, ct))
                continue;

            var relativeFile = Path.GetRelativePath(config.Root, resolvedFile);
            var lines = await File.ReadAllLinesAsync(resolvedFile, Encoding.UTF8, ct);

            for (var lineNumber = 0; lineNumber < lines.Length && matchCount < maxMatches; lineNumber++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!regex.IsMatch(lines[lineNumber]))
                        continue;

                    sb.Append(relativeFile);
                    sb.Append(':');
                    sb.Append(lineNumber + 1);
                    sb.Append(':');
                    sb.AppendLine(lines[lineNumber]);
                    matchCount++;
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
        }

        if (matchCount >= maxMatches)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"... truncated at {maxMatches} matches");

        return sb.ToString();
    }

    private static ToolResult Success(string toolName, string output, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = output, Duration = sw.Elapsed };
    }

    private static ToolResult Failure(string toolName, string error, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = false, ToolName = toolName, Error = error, Duration = sw.Elapsed };
    }
}
