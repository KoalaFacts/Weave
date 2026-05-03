using System.Diagnostics;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

internal static class FileSystemToolResultFactory
{
    public static ToolResult Success(string toolName, string output, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = output, Duration = sw.Elapsed };
    }

    public static ToolResult Failure(string toolName, string error, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = false, ToolName = toolName, Error = error, Duration = sw.Elapsed };
    }
}
