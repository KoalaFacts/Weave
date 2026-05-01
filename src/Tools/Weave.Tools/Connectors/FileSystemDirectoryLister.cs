using System.Diagnostics;
using System.Text;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal sealed class FileSystemDirectoryLister
{
    private readonly FileSystemToolResultFactory _result;

    public FileSystemDirectoryLister(FileSystemToolResultFactory result) => _result = result;

    public ToolResult List(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
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
        { return _result.Failure(toolName, ex.Message, sw); }

        if (!Directory.Exists(fullPath))
            return _result.Failure(toolName, $"Directory not found: {relativePath}", sw);

        var output = BuildListing(fullPath);
        return _result.Success(toolName, output, sw);
    }

    private static string BuildListing(string fullPath)
    {
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
                continue;
            }

            var fileInfo = new FileInfo(entry);
            sb.Append("[file] ");
            sb.Append(fileInfo.Length);
            sb.Append("  ");
            sb.AppendLine(Path.GetFileName(entry));
        }

        return sb.ToString();
    }
}
