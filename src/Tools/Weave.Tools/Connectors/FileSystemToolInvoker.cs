using System.Diagnostics;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal sealed class FileSystemToolInvoker
{
    private readonly FileSystemFileReader _reader;
    private readonly FileSystemFileWriter _writer;
    private readonly FileSystemDirectoryLister _directoryLister;
    private readonly FileSystemSearchOperations _search;
    private readonly FileSystemFileInspector _inspector;
    private readonly FileSystemFileEditor _editor;
    private readonly FileSystemToolResultFactory _result;

    public FileSystemToolInvoker()
    {
        _result = new FileSystemToolResultFactory();
        _reader = new FileSystemFileReader(_result);
        _writer = new FileSystemFileWriter(_result);
        _directoryLister = new FileSystemDirectoryLister(_result);
        _search = new FileSystemSearchOperations();
        _inspector = new FileSystemFileInspector(_result);
        _editor = new FileSystemFileEditor(_result);
    }

    public async Task<ToolResult> InvokeAsync(
        string toolName,
        FileSystemToolConfig config,
        ToolInvocation invocation,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (invocation.Method.Equals("read_file", StringComparison.OrdinalIgnoreCase))
            return await _reader.ReadAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("write_file", StringComparison.OrdinalIgnoreCase))
            return await _writer.WriteAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("list_directory", StringComparison.OrdinalIgnoreCase))
            return _directoryLister.List(toolName, config, invocation, sw);

        if (invocation.Method.Equals("search_files", StringComparison.OrdinalIgnoreCase))
            return _search.SearchFiles(toolName, config, invocation, sw);

        if (invocation.Method.Equals("file_info", StringComparison.OrdinalIgnoreCase))
            return _inspector.GetInfo(toolName, config, invocation, sw);

        if (invocation.Method.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
            return await _editor.EditAsync(toolName, config, invocation, sw, ct);

        if (invocation.Method.Equals("grep", StringComparison.OrdinalIgnoreCase))
            return await _search.GrepAsync(toolName, config, invocation, sw, ct);

        return _result.Failure(
            toolName,
            $"Unknown method '{invocation.Method}'. Supported methods: read_file, write_file, edit_file, list_directory, search_files, grep, file_info",
            sw);
    }
}
