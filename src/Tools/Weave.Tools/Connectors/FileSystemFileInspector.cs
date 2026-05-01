using System.Diagnostics;
using System.Globalization;
using System.Text;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal static class FileSystemFileInspector
{
    public static ToolResult GetInfo(string toolName, FileSystemToolConfig config, ToolInvocation invocation, Stopwatch sw)
    {
        if (!invocation.Parameters.TryGetValue("path", out var relativePath) || string.IsNullOrEmpty(relativePath))
            return FileSystemToolResultFactory.Failure(toolName, "Parameter 'path' is required for file_info", sw);

        string fullPath;
        try
        { fullPath = FileSystemPathGuard.ResolveSafePath(config.Root, relativePath, config.Sandbox); }
        catch (ArgumentException ex)
        { return FileSystemToolResultFactory.Failure(toolName, ex.Message, sw); }

        var fileExists = File.Exists(fullPath);
        var dirExists = Directory.Exists(fullPath);
        var output = BuildInfo(relativePath, fullPath, fileExists, dirExists);
        return FileSystemToolResultFactory.Success(toolName, output, sw);
    }

    private static string BuildInfo(string relativePath, string fullPath, bool fileExists, bool dirExists)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Path: {relativePath}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Exists: {fileExists || dirExists}"));

        if (fileExists)
            AppendFileInfo(sb, fullPath);
        else if (dirExists)
            AppendDirectoryInfo(sb, fullPath);

        return sb.ToString();
    }

    private static void AppendFileInfo(StringBuilder sb, string fullPath)
    {
        var fileInfo = new FileInfo(fullPath);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Size: {fileInfo.Length}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"LastModifiedUtc: {fileInfo.LastWriteTimeUtc:O}"));
        sb.AppendLine("IsDirectory: False");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"IsReadOnly: {fileInfo.IsReadOnly}"));
    }

    private static void AppendDirectoryInfo(StringBuilder sb, string fullPath)
    {
        var dirInfo = new DirectoryInfo(fullPath);
        sb.AppendLine("Size: N/A");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"LastModifiedUtc: {dirInfo.LastWriteTimeUtc:O}"));
        sb.AppendLine("IsDirectory: True");
        sb.AppendLine("IsReadOnly: False");
    }
}
