using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed partial class FileSystemToolConnector(ILogger<FileSystemToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, FileSystemToolConfig> _configurations = new(StringComparer.Ordinal);

    public ToolType ToolType => ToolType.FileSystem;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var config = tool.FileSystem ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no FileSystem configuration");

        if (string.IsNullOrWhiteSpace(config.Root))
            throw new InvalidOperationException($"Tool '{tool.Name}': FileSystem 'root' is required");

        var resolvedRoot = Path.GetFullPath(config.Root);
        var maxReadBytes = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        var resolvedConfig = config with { Root = resolvedRoot, MaxReadBytes = maxReadBytes };
        var connectionId = $"fs:{tool.Name}:{Guid.NewGuid():N}";
        _configurations[connectionId] = resolvedConfig;

        LogFileSystemToolConnected(tool.Name, resolvedRoot);

        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.FileSystem,
            ConnectionId = connectionId,
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        _configurations.TryRemove(handle.ConnectionId, out _);
        LogFileSystemToolDisconnected(handle.ToolName);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_configurations.TryGetValue(handle.ConnectionId, out var config))
        {
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = "FileSystem tool is not connected"
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (invocation.Method.Equals("read_file", StringComparison.OrdinalIgnoreCase))
                return await ReadFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("write_file", StringComparison.OrdinalIgnoreCase))
                return await WriteFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("list_directory", StringComparison.OrdinalIgnoreCase))
                return ListDirectory(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("search_files", StringComparison.OrdinalIgnoreCase))
                return SearchFiles(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("file_info", StringComparison.OrdinalIgnoreCase))
                return GetFileInfo(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
                return await EditFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("grep", StringComparison.OrdinalIgnoreCase))
                return await GrepAsync(handle.ToolName, config, invocation, sw, ct);

            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"Unknown method '{invocation.Method}'. Supported methods: read_file, write_file, edit_file, list_directory, search_files, grep, file_info",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = "Sandboxed file system tool. Methods: read_file, write_file, edit_file, list_directory, search_files, grep, file_info",
            Parameters =
            [
                new ToolParameter { Name = "path", Type = "string", Description = "Relative path within the sandboxed root", Required = true },
                new ToolParameter { Name = "pattern", Type = "string", Description = "Regex pattern for grep, or glob pattern for search_files" },
                new ToolParameter { Name = "old_string", Type = "string", Description = "Text to find for edit_file" },
                new ToolParameter { Name = "new_string", Type = "string", Description = "Replacement text for edit_file" },
                new ToolParameter { Name = "replace_all", Type = "string", Description = "Set to 'true' to replace all occurrences in edit_file" },
                new ToolParameter { Name = "glob", Type = "string", Description = "File glob filter for grep (default: *)" },
                new ToolParameter { Name = "case_insensitive", Type = "string", Description = "Set to 'true' for case-insensitive grep" }
            ]
        });
    }

    private static async Task<ToolResult> ReadFileAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'path' is required for read_file", Duration = sw.Elapsed };
        }

        string fullPath;
        try
        {
            fullPath = ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = ex.Message, Duration = sw.Elapsed };
        }

        if (!File.Exists(fullPath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = $"File not found: {relativePath}", Duration = sw.Elapsed };
        }

        var info = new FileInfo(fullPath);
        if (info.Length > config.MaxReadBytes)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = toolName,
                Error = $"File size ({info.Length} bytes) exceeds the read limit ({config.MaxReadBytes} bytes)",
                Duration = sw.Elapsed
            };
        }

        if (await IsBinaryFileAsync(fullPath, ct))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "File appears to be binary and cannot be read as text", Duration = sw.Elapsed };
        }

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = content, Duration = sw.Elapsed };
    }

    private static async Task<ToolResult> WriteFileAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (config.ReadOnly)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "FileSystem tool is configured as read-only", Duration = sw.Elapsed };
        }

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'path' is required for write_file", Duration = sw.Elapsed };
        }

        if (invocation.RawInput is null)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "RawInput is required for write_file", Duration = sw.Elapsed };
        }

        string fullPath;
        try
        {
            fullPath = ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = ex.Message, Duration = sw.Elapsed };
        }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        await File.WriteAllTextAsync(fullPath, invocation.RawInput, Encoding.UTF8, ct);
        var bytesWritten = Encoding.UTF8.GetByteCount(invocation.RawInput);
        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = $"Wrote {bytesWritten} bytes to {relativePath}", Duration = sw.Elapsed };
    }

    private static ToolResult ListDirectory(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw)
    {
        invocation.Parameters.TryGetValue("path", out var relativePath);
        relativePath ??= string.Empty;

        string fullPath;
        try
        {
            fullPath = string.IsNullOrEmpty(relativePath)
                ? config.Root
                : ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = ex.Message, Duration = sw.Elapsed };
        }

        if (!Directory.Exists(fullPath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = $"Directory not found: {relativePath}", Duration = sw.Elapsed };
        }

        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(entry))
            {
                sb.Append("[dir]  ");
                sb.AppendLine(Path.GetFileName(entry) + "/");
            }
            else
            {
                var fi = new FileInfo(entry);
                sb.Append("[file] ");
                sb.Append(fi.Length);
                sb.Append("  ");
                sb.AppendLine(Path.GetFileName(entry));
            }
        }

        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = sb.ToString(), Duration = sw.Elapsed };
    }

    private static ToolResult SearchFiles(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'pattern' is required for search_files", Duration = sw.Elapsed };
        }

        if (pattern.Contains("..", StringComparison.Ordinal))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Pattern must not contain '..'", Duration = sw.Elapsed };
        }

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(config.Root, pattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(config.Root, file);
            results.Add(relative);
            if (results.Count >= 1000)
                break;
        }

        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = string.Join("\n", results), Duration = sw.Elapsed };
    }

    private static ToolResult GetFileInfo(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'path' is required for file_info", Duration = sw.Elapsed };
        }

        string fullPath;
        try
        {
            fullPath = ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = ex.Message, Duration = sw.Elapsed };
        }

        var fileExists = File.Exists(fullPath);
        var dirExists = Directory.Exists(fullPath);
        var exists = fileExists || dirExists;

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Path: {relativePath}"));
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Exists: {exists}"));

        if (fileExists)
        {
            var fi = new FileInfo(fullPath);
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Size: {fi.Length}"));
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"LastModifiedUtc: {fi.LastWriteTimeUtc:O}"));
            sb.AppendLine("IsDirectory: False");
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IsReadOnly: {fi.IsReadOnly}"));
        }
        else if (dirExists)
        {
            var di = new DirectoryInfo(fullPath);
            sb.AppendLine("Size: N/A");
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"LastModifiedUtc: {di.LastWriteTimeUtc:O}"));
            sb.AppendLine("IsDirectory: True");
            sb.AppendLine("IsReadOnly: False");
        }

        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = sb.ToString(), Duration = sw.Elapsed };
    }

    private static async Task<ToolResult> EditFileAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (config.ReadOnly)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "FileSystem tool is configured as read-only", Duration = sw.Elapsed };
        }

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'path' is required for edit_file", Duration = sw.Elapsed };
        }

        if (!invocation.Parameters.TryGetValue("old_string", out var oldString) || string.IsNullOrEmpty(oldString))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'old_string' is required for edit_file", Duration = sw.Elapsed };
        }

        if (!invocation.Parameters.TryGetValue("new_string", out var newString))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'new_string' is required for edit_file", Duration = sw.Elapsed };
        }

        string fullPath;
        try
        {
            fullPath = ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = ex.Message, Duration = sw.Elapsed };
        }

        if (!File.Exists(fullPath))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = $"File not found: {relativePath}", Duration = sw.Elapsed };
        }

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        var occurrences = CountOccurrences(content, oldString);

        if (occurrences == 0)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "old_string not found in file", Duration = sw.Elapsed };
        }

        var replaceAll = invocation.Parameters.TryGetValue("replace_all", out var replaceAllStr)
            && replaceAllStr.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!replaceAll && occurrences > 1)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = toolName,
                Error = $"old_string found {occurrences} times. Set replace_all=true to replace all, or provide a more specific old_string.",
                Duration = sw.Elapsed
            };
        }

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);

        await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8, ct);
        sw.Stop();
        return new ToolResult
        {
            Success = true,
            ToolName = toolName,
            Output = $"Replaced {(replaceAll ? occurrences : 1)} occurrence(s) in {relativePath}",
            Duration = sw.Elapsed
        };
    }

    private static async Task<ToolResult> GrepAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Parameter 'pattern' is required for grep", Duration = sw.Elapsed };
        }

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled;
            if (invocation.Parameters.TryGetValue("case_insensitive", out var ci)
                && ci.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                options |= RegexOptions.IgnoreCase;
            }
            regex = new Regex(pattern, options, matchTimeout: TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException ex)
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = $"Invalid regex pattern: {ex.Message}", Duration = sw.Elapsed };
        }

        // Determine file scope: specific path or glob
        var glob = invocation.Parameters.GetValueOrDefault("glob", "*");
        if (glob.Contains("..", StringComparison.Ordinal))
        {
            sw.Stop();
            return new ToolResult { Success = false, ToolName = toolName, Error = "Glob must not contain '..'", Duration = sw.Elapsed };
        }

        var sb = new StringBuilder();
        var matchCount = 0;
        const int maxMatches = 500;

        foreach (var file in Directory.EnumerateFiles(config.Root, glob, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (matchCount >= maxMatches)
                break;

            var relativFile = Path.GetRelativePath(config.Root, file);
            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, Encoding.UTF8, ct);
            }
            catch
            {
                continue; // Skip unreadable files (binary, locked, etc.)
            }

            for (var lineNum = 0; lineNum < lines.Length; lineNum++)
            {
                if (matchCount >= maxMatches)
                    break;

                if (regex.IsMatch(lines[lineNum]))
                {
                    sb.Append(relativFile);
                    sb.Append(':');
                    sb.Append(lineNum + 1);
                    sb.Append(':');
                    sb.AppendLine(lines[lineNum]);
                    matchCount++;
                }
            }
        }

        if (matchCount >= maxMatches)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"... truncated at {maxMatches} matches");

        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = sb.ToString(), Duration = sw.Elapsed };
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
            return text;
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    internal static string ResolveSafePath(string root, string relativePath, bool sandbox = true)
    {
        if (relativePath.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Path contains null bytes");

        relativePath = relativePath.Replace('\\', '/');

        if (relativePath.StartsWith('/'))
            throw new ArgumentException("Path must be relative, not absolute");

        if (relativePath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Path contains a URL scheme");

        if (relativePath.Length >= 2 && relativePath[1] == ':')
            throw new ArgumentException("Path contains a drive letter");

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        VerifyContainment(root, fullPath);

        if (sandbox)
            VerifyNoSymlinkEscape(root, fullPath);

        return fullPath;
    }

    private static void VerifyContainment(string root, string fullPath)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path traversal detected");
        }
    }

    /// <summary>
    /// Walk every existing component of the path and verify that no symlink/junction
    /// redirects outside the sandbox root. This closes the symlink escape attack vector.
    /// </summary>
    private static void VerifyNoSymlinkEscape(string root, string fullPath)
    {
        // Walk from root downward through each path component that exists on disk.
        // At each step, if the component is a symlink/junction, resolve it and verify
        // the target is still under root.
        var relativePart = Path.GetRelativePath(root, fullPath);
        if (relativePart == ".")
            return;

        var segments = relativePart.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if (!Path.Exists(current))
                break; // Remaining components don't exist yet (e.g. write to new file) — that's fine

            var fileInfo = new FileInfo(current);
            if (fileInfo.LinkTarget is not null)
            {
                // Resolve the final target of the symlink chain
                var resolved = File.ResolveLinkTarget(current, returnFinalTarget: true)
                    ?? throw new ArgumentException($"Sandbox violation: symlink at '{segment}' could not be resolved");

                var resolvedPath = Path.GetFullPath(resolved.FullName);
                VerifyContainment(root, resolvedPath);

                // Continue walking from the resolved target
                current = resolvedPath;
            }

            // Also check if it's a directory junction (Windows) — these report as directories, not symlinks
            if (Directory.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null)
                {
                    var resolved = Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                        ?? throw new ArgumentException($"Sandbox violation: junction at '{segment}' could not be resolved");

                    var resolvedPath = Path.GetFullPath(resolved.FullName);
                    VerifyContainment(root, resolvedPath);

                    current = resolvedPath;
                }
            }
        }
    }

    private static async Task<bool> IsBinaryFileAsync(string fullPath, CancellationToken ct)
    {
        var buffer = new byte[8192];
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var bytesRead = await stream.ReadAsync(buffer, ct);
        for (var i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == 0x00)
                return true;
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "FileSystem tool '{Tool}' connected (root: {Root})")]
    private partial void LogFileSystemToolConnected(string tool, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "FileSystem tool '{Tool}' disconnected")]
    private partial void LogFileSystemToolDisconnected(string tool);
}
