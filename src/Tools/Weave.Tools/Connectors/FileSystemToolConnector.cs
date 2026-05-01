using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed partial class FileSystemToolConnector(ILogger<FileSystemToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, FileSystemToolConfig> _configurations = new(StringComparer.Ordinal);
    private readonly FileSystemToolOperations _operations = new();

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
                return await _operations.ReadFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("write_file", StringComparison.OrdinalIgnoreCase))
                return await _operations.WriteFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("list_directory", StringComparison.OrdinalIgnoreCase))
                return _operations.ListDirectory(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("search_files", StringComparison.OrdinalIgnoreCase))
                return _operations.SearchFiles(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("file_info", StringComparison.OrdinalIgnoreCase))
                return _operations.GetFileInfo(handle.ToolName, config, invocation, sw);

            if (invocation.Method.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
                return await _operations.EditFileAsync(handle.ToolName, config, invocation, sw, ct);

            if (invocation.Method.Equals("grep", StringComparison.OrdinalIgnoreCase))
                return await _operations.GrepAsync(handle.ToolName, config, invocation, sw, ct);

            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"Unknown method '{invocation.Method}'. Supported methods: read_file, write_file, edit_file, list_directory, search_files, grep, file_info",
                Duration = sw.Elapsed
            };
        }
        catch (ArgumentException ex)
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
        catch (Exception ex)
        {
            sw.Stop();
            // Sanitize: strip root path from error messages to avoid leaking host filesystem layout
            var safeMessage = config.Root.Length > 0
                ? ex.Message.Replace(config.Root, "[sandbox]", StringComparison.OrdinalIgnoreCase)
                : ex.Message;
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = safeMessage,
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

    [LoggerMessage(Level = LogLevel.Information, Message = "FileSystem tool '{Tool}' connected (root: {Root})")]
    private partial void LogFileSystemToolConnected(string tool, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "FileSystem tool '{Tool}' disconnected")]
    private partial void LogFileSystemToolDisconnected(string tool);
}
