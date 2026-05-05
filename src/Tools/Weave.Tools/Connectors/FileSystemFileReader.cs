using System.Diagnostics;
using System.Text;
using Weave.Tools.Tool;
namespace Weave.Tools.Connectors;

internal static class FileSystemFileReader
{
    public static async Task<ToolResult> ReadAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return FileSystemToolResultFactory.Failure(toolName, "Parameter 'path' is required for read_file", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return FileSystemToolResultFactory.Failure(toolName, ex.Message, sw); }

        if (!File.Exists(fullPath))
            return FileSystemToolResultFactory.Failure(toolName, $"File not found: {relativePath}", sw);

        var info = new FileInfo(fullPath);
        if (info.Length > config.MaxReadBytes)
            return FileSystemToolResultFactory.Failure(toolName, $"File size ({info.Length} bytes) exceeds the read limit ({config.MaxReadBytes} bytes)", sw);

        if (await FileSystemTextFile.IsBinaryFileAsync(fullPath, ct))
            return FileSystemToolResultFactory.Failure(toolName, "File appears to be binary and cannot be read as text", sw);

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        return FileSystemToolResultFactory.Success(toolName, content, sw);
    }
}
