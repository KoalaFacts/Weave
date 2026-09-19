using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weave.Tools.Tool;

namespace Weave.Invocations;

internal static class FileWritePlanBinding
{
    public static FileWriteApprovalPlan? Create(InvocationRecord candidate, ToolSpec spec, ToolInvocation request)
    {
        if (spec.Type != ToolType.FileSystem || request.Method != "write_file"
            || spec.FileSystem is not { ReadOnly: false, Sandbox: true } config
            || request.Parameters.Count != 1 || !request.Parameters.TryGetValue("path", out var name)
            || string.IsNullOrEmpty(name) || name.Length > 128 || name is "." or ".."
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')
            || request.RawInput is not { } content || content.Contains("${", StringComparison.Ordinal))
            return null;
        var limit = config.MaxReadBytes > 0 ? config.MaxReadBytes : 1_048_576;
        if (Encoding.UTF8.GetByteCount(content) > Math.Min(limit, 16_384))
            return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        // v0 deliberately supports a single file in an application-controlled root,
        // not arbitrary path traversal or approval of symlink destinations.
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if (!directory.Exists || directory.LinkTarget is not null
                || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        var file = new FileInfo(Path.Combine(root, name));
        if (file.LinkTarget is not null || Directory.Exists(file.FullName)
            || file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;
        return new FileWriteApprovalPlan(candidate.InvocationId, candidate.WorkspaceId, candidate.Subject,
            candidate.ToolName, root, name, content, limit, "filesystem/write_file/v1");
    }

    public static string Serialize(FileWriteApprovalPlan plan) =>
        JsonSerializer.Serialize(plan, ApprovalPlanJsonContext.Default.FileWriteApprovalPlan);

    public static string Digest(FileWriteApprovalPlan plan) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(plan))));

    public static ToolInvocation Request(FileWriteApprovalPlan plan) => new()
    {
        InvocationId = plan.InvocationId,
        ToolName = plan.ToolName,
        Method = "write_file",
        Parameters = new() { ["path"] = plan.FileName },
        RawInput = plan.Content
    };
}
