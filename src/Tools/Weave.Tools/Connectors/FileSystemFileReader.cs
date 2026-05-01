using System.Diagnostics;
using System.Text;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal sealed class FileSystemFileReader
{
    private readonly FileSystemToolResultFactory _result;

    public FileSystemFileReader(FileSystemToolResultFactory result) => _result = result;

    public async Task<ToolResult> ReadAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return _result.Failure(toolName, "Parameter 'path' is required for read_file", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return _result.Failure(toolName, ex.Message, sw); }

        if (!File.Exists(fullPath))
            return _result.Failure(toolName, $"File not found: {relativePath}", sw);

        var info = new FileInfo(fullPath);
        if (info.Length > config.MaxReadBytes)
            return _result.Failure(toolName, $"File size ({info.Length} bytes) exceeds the read limit ({config.MaxReadBytes} bytes)", sw);

        if (await FileSystemTextFile.IsBinaryFileAsync(fullPath, ct))
            return _result.Failure(toolName, "File appears to be binary and cannot be read as text", sw);

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        return _result.Success(toolName, content, sw);
    }
}
