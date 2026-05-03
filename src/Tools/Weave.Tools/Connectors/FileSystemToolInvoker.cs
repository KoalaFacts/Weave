using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal static class FileSystemToolInvoker
{
    public static async Task<ToolResult> InvokeAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (invocation.Method.Equals("read_file", StringComparison.OrdinalIgnoreCase))
            return await FileSystemFileReader.ReadAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("write_file", StringComparison.OrdinalIgnoreCase))
            return await FileSystemFileWriter.WriteAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("list_directory", StringComparison.OrdinalIgnoreCase))
            return FileSystemDirectoryLister.List(toolName, config, invocation, sw);

        if (invocation.Method.Equals("search_files", StringComparison.OrdinalIgnoreCase))
            return SearchFiles(toolName, config, invocation, sw);

        if (invocation.Method.Equals("file_info", StringComparison.OrdinalIgnoreCase))
            return FileSystemFileInspector.GetInfo(toolName, config, invocation, sw);

        if (invocation.Method.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
            return await FileSystemFileEditor.EditAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("grep", StringComparison.OrdinalIgnoreCase))
            return await GrepAsync(toolName, config, invocation, sw, ct);

        return FileSystemToolResultFactory.Failure(
            toolName,
            $"Unknown method '{invocation.Method}'. Supported methods: read_file, write_file, edit_file, list_directory, search_files, grep, file_info",
            sw);
    }

    private static ToolResult SearchFiles(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
            return FileSystemToolResultFactory.Failure(toolName, "Parameter 'pattern' is required for search_files", sw);

        if (pattern.Contains("..", StringComparison.Ordinal))
            return FileSystemToolResultFactory.Failure(toolName, "Pattern must not contain '..'", sw);

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(config.Root, pattern, SearchOption.AllDirectories))
        {
            var resolvedFile = Path.GetFullPath(file);
            try
            { FileSystemPathGuard.VerifyContainment(config.Root, resolvedFile); }
            catch (ArgumentException)
            { continue; }

            results.Add(Path.GetRelativePath(config.Root, resolvedFile));
            if (results.Count >= 1000)
                break;
        }

        return FileSystemToolResultFactory.Success(toolName, string.Join("\n", results), sw);
    }

    private static async Task<ToolResult> GrepAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
            return FileSystemToolResultFactory.Failure(toolName, "Parameter 'pattern' is required for grep", sw);

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
        { return FileSystemToolResultFactory.Failure(toolName, $"Invalid regex pattern: {ex.Message}", sw); }

        var glob = invocation.Parameters.GetValueOrDefault("glob", "*");
        if (glob.Contains("..", StringComparison.Ordinal))
            return FileSystemToolResultFactory.Failure(toolName, "Glob must not contain '..'", sw);

        var output = await GrepFilesAsync(config, regex, glob, ct);
        return FileSystemToolResultFactory.Success(toolName, output, sw);
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
            catch (ArgumentException)
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
}
