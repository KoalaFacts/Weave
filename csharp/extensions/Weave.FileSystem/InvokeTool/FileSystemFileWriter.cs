using System.Diagnostics;
using System.Text;
using Weave.Tools.Tool;
namespace Weave.Tools.Connectors;

internal static class FileSystemFileWriter
{
    public static async Task<ToolResult> WriteAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (config.ReadOnly)
            return FileSystemToolResultFactory.Failure(toolName, "FileSystem tool is configured as read-only", sw);

        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return FileSystemToolResultFactory.Failure(toolName, "Parameter 'path' is required for write_file", sw);

        if (invocation.RawInput is null)
            return FileSystemToolResultFactory.Failure(toolName, "RawInput is required for write_file", sw);

        var writeBytes = Encoding.UTF8.GetByteCount(invocation.RawInput);
        var maxWrite = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        if (writeBytes > maxWrite)
            return FileSystemToolResultFactory.Failure(toolName, $"Write size ({writeBytes} bytes) exceeds the limit ({maxWrite} bytes)", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return FileSystemToolResultFactory.Failure(toolName, ex.Message, sw); }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        await File.WriteAllTextAsync(fullPath, invocation.RawInput, Encoding.UTF8, ct);
        return FileSystemToolResultFactory.Success(toolName, $"Wrote {writeBytes} bytes to {relativePath}", sw);
    }
}
