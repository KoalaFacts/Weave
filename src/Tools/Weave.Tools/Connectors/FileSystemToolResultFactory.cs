using System.Diagnostics;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from file system operation classes.")]
internal sealed class FileSystemToolResultFactory
{
    public ToolResult Success(string toolName, string output, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = true, ToolName = toolName, Output = output, Duration = sw.Elapsed };
    }

    public ToolResult Failure(string toolName, string error, Stopwatch sw)
    {
        sw.Stop();
        return new ToolResult { Success = false, ToolName = toolName, Error = error, Duration = sw.Elapsed };
    }
}
