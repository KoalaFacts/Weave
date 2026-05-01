using System.Diagnostics;
using System.Text;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for connector testability.")]
internal sealed class FileSystemToolOperations
{
    private readonly FileSystemSearchOperations _searchOperations = new();

    public async Task<ToolResult> ReadFileAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return Failure(toolName, "Parameter 'path' is required for read_file", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return Failure(toolName, ex.Message, sw); }

        if (!File.Exists(fullPath))
            return Failure(toolName, $"File not found: {relativePath}", sw);

        var info = new FileInfo(fullPath);
        if (info.Length > config.MaxReadBytes)
            return Failure(toolName, $"File size ({info.Length} bytes) exceeds the read limit ({config.MaxReadBytes} bytes)", sw);

        if (await FileSystemTextFile.IsBinaryFileAsync(fullPath, ct))
            return Failure(toolName, "File appears to be binary and cannot be read as text", sw);

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        return Success(toolName, content, sw);
    }

    public async Task<ToolResult> WriteFileAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
    {
        if (config.ReadOnly)
            return Failure(toolName, "FileSystem tool is configured as read-only", sw);

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return Failure(toolName, "Parameter 'path' is required for write_file", sw);

        if (invocation.RawInput is null)
            return Failure(toolName, "RawInput is required for write_file", sw);

        var writeBytes = Encoding.UTF8.GetByteCount(invocation.RawInput);
        var maxWrite = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        if (writeBytes > maxWrite)
            return Failure(toolName, $"Write size ({writeBytes} bytes) exceeds the limit ({maxWrite} bytes)", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return Failure(toolName, ex.Message, sw); }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        await File.WriteAllTextAsync(fullPath, invocation.RawInput, Encoding.UTF8, ct);
        return Success(toolName, $"Wrote {writeBytes} bytes to {relativePath}", sw);
    }

    public ToolResult ListDirectory(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
    {
        invocation.Parameters.TryGetValue("path", out var relativePath);
        relativePath ??= string.Empty;

        string fullPath;
        try
        {
            fullPath = string.IsNullOrEmpty(relativePath)
                ? config.Root
                : FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox);
        }
        catch (ArgumentException ex)
        { return Failure(toolName, ex.Message, sw); }

        if (!Directory.Exists(fullPath))
            return Failure(toolName, $"Directory not found: {relativePath}", sw);

        var sb = new StringBuilder();
        var entryCount = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (++entryCount > 1000)
            {
                sb.AppendLine("... truncated at 1000 entries");
                break;
            }

            if (Directory.Exists(entry))
            {
                sb.Append("[dir]  ");
                sb.AppendLine(Path.GetFileName(entry) + "/");
            }
            else
            {
                var fileInfo = new FileInfo(entry);
                sb.Append("[file] ");
                sb.Append(fileInfo.Length);
                sb.Append("  ");
                sb.AppendLine(Path.GetFileName(entry));
            }
        }

        return Success(toolName, sb.ToString(), sw);
    }

    public ToolResult SearchFiles(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
        => _searchOperations.SearchFiles(toolName, config, invocation, sw);

    public ToolResult GetFileInfo(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return Failure(toolName, "Parameter 'path' is required for file_info", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return Failure(toolName, ex.Message, sw); }

        var fileExists = File.Exists(fullPath);
        var dirExists = Directory.Exists(fullPath);
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Path: {relativePath}"));
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Exists: {fileExists || dirExists}"));

        if (fileExists)
        {
            var fileInfo = new FileInfo(fullPath);
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Size: {fileInfo.Length}"));
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"LastModifiedUtc: {fileInfo.LastWriteTimeUtc:O}"));
            sb.AppendLine("IsDirectory: False");
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IsReadOnly: {fileInfo.IsReadOnly}"));
        }
        else if (dirExists)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            sb.AppendLine("Size: N/A");
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"LastModifiedUtc: {dirInfo.LastWriteTimeUtc:O}"));
            sb.AppendLine("IsDirectory: True");
            sb.AppendLine("IsReadOnly: False");
        }

        return Success(toolName, sb.ToString(), sw);
    }

    public async Task<ToolResult> EditFileAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
    {
        if (config.ReadOnly)
            return Failure(toolName, "FileSystem tool is configured as read-only", sw);

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return Failure(toolName, "Parameter 'path' is required for edit_file", sw);

        if (!invocation.Parameters.TryGetValue("old_string", out var oldString) || string.IsNullOrEmpty(oldString))
            return Failure(toolName, "Parameter 'old_string' is required for edit_file", sw);

        if (!invocation.Parameters.TryGetValue("new_string", out var newString))
            return Failure(toolName, "Parameter 'new_string' is required for edit_file", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return Failure(toolName, ex.Message, sw); }

        if (!File.Exists(fullPath))
            return Failure(toolName, $"File not found: {relativePath}", sw);

        var fileSize = new FileInfo(fullPath).Length;
        var maxRead = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        if (fileSize > maxRead)
            return Failure(toolName, $"File size ({fileSize} bytes) exceeds the read limit ({maxRead} bytes)", sw);

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        var occurrences = CountOccurrences(content, oldString);

        if (occurrences == 0)
            return Failure(toolName, "old_string not found in file", sw);

        var replaceAll = invocation.Parameters.TryGetValue("replace_all", out var replaceAllStr)
            && replaceAllStr.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!replaceAll && occurrences > 1)
            return Failure(toolName, $"old_string found {occurrences} times. Set replace_all=true to replace all, or provide a more specific old_string.", sw);

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);

        await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8, ct);
        return Success(toolName, $"Replaced {(replaceAll ? occurrences : 1)} occurrence(s) in {relativePath}", sw);
    }

    public async Task<ToolResult> GrepAsync(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw, CancellationToken ct)
        => await _searchOperations.GrepAsync(toolName, config, invocation, sw, ct);

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
