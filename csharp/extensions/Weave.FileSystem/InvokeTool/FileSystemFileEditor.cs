using System.Diagnostics;
using System.Text;
using Weave.Tools.Tool;
namespace Weave.Tools.Connectors;

internal static class FileSystemFileEditor
{
    public static async Task<ToolResult> EditAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (config.ReadOnly)
            return FileSystemToolResultFactory.Failure(toolName, "FileSystem tool is configured as read-only", sw);

        if (!TryReadEditRequest(toolName, invocation, sw, out var request, out var failure))
            return failure;

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, request.RelativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return FileSystemToolResultFactory.Failure(toolName, ex.Message, sw); }

        if (!File.Exists(fullPath))
            return FileSystemToolResultFactory.Failure(toolName, $"File not found: {request.RelativePath}", sw);

        var maxRead = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        var fileSize = new FileInfo(fullPath).Length;
        if (fileSize > maxRead)
            return FileSystemToolResultFactory.Failure(toolName, $"File size ({fileSize} bytes) exceeds the read limit ({maxRead} bytes)", sw);

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        var occurrences = CountOccurrences(content, request.OldString);
        if (occurrences == 0)
            return FileSystemToolResultFactory.Failure(toolName, "old_string not found in file", sw);

        if (!request.ReplaceAll && occurrences > 1)
            return FileSystemToolResultFactory.Failure(toolName, $"old_string found {occurrences} times. Set replace_all=true to replace all, or provide a more specific old_string.", sw);

        var updated = request.ReplaceAll
            ? content.Replace(request.OldString, request.NewString, StringComparison.Ordinal)
            : ReplaceFirst(content, request.OldString, request.NewString);

        await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8, ct);
        return FileSystemToolResultFactory.Success(toolName, $"Replaced {(request.ReplaceAll ? occurrences : 1)} occurrence(s) in {request.RelativePath}", sw);
    }

    private static bool TryReadEditRequest(
        string toolName,
        ToolInvocation invocation,
        Stopwatch sw,
        out EditFileRequest request,
        out ToolResult failure)
    {
        request = default;

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
        {
            failure = FileSystemToolResultFactory.Failure(toolName, "Parameter 'path' is required for edit_file", sw);
            return false;
        }

        if (!invocation.Parameters.TryGetValue("old_string", out var oldString) || string.IsNullOrEmpty(oldString))
        {
            failure = FileSystemToolResultFactory.Failure(toolName, "Parameter 'old_string' is required for edit_file", sw);
            return false;
        }

        if (!invocation.Parameters.TryGetValue("new_string", out var newString))
        {
            failure = FileSystemToolResultFactory.Failure(toolName, "Parameter 'new_string' is required for edit_file", sw);
            return false;
        }

        var replaceAll = invocation.Parameters.TryGetValue("replace_all", out var replaceAllStr)
            && replaceAllStr.Equals("true", StringComparison.OrdinalIgnoreCase);

        request = new EditFileRequest(relativePath, oldString, newString, replaceAll);
        failure = default!;
        return true;
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

    private readonly record struct EditFileRequest(
        string RelativePath,
        string OldString,
        string NewString,
        bool ReplaceAll);
}
