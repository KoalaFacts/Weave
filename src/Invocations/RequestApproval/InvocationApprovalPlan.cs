using System.Security.Cryptography;
using System.Text.Json;

namespace Weave.Invocations;

/// <summary>Versioned evidence for the bounded approval contract; no raw payload is retained.</summary>
public static class InvocationApprovalPlan
{
    public static string? BindTarget(string? adapterTargetDigest, string? effectiveInputDigest)
    {
        if (string.IsNullOrWhiteSpace(adapterTargetDigest) || adapterTargetDigest.Length > 1024
            || string.IsNullOrWhiteSpace(effectiveInputDigest))
            return null;
        return "target-v1:" + Hash(writer =>
        {
            writer.WriteStringValue(adapterTargetDigest);
            writer.WriteStringValue(effectiveInputDigest);
        });
    }

    public static InvocationApproval Create(InvocationRecord candidate, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ApprovalTargetDigest);
        var expires = candidate.CreatedAt + lifetime;
        var digest = "approval-v1:" + Hash(writer =>
        {
            writer.WriteStringValue(candidate.InvocationId.ToString());
            writer.WriteStringValue(candidate.WorkspaceId);
            writer.WriteStringValue(candidate.Subject);
            writer.WriteStringValue(candidate.ToolName);
            writer.WriteStringValue(candidate.Operation);
            writer.WriteStringValue(candidate.InputDigest);
            writer.WriteStringValue(candidate.ApprovalTargetDigest);
            writer.WriteStringValue(candidate.CreatedAt);
            writer.WriteStringValue(expires);
        });
        return new InvocationApproval(candidate.InvocationId, candidate.WorkspaceId, candidate.Subject,
            candidate.ToolName, candidate.Operation, candidate.InputDigest, candidate.ApprovalTargetDigest,
            digest, candidate.CreatedAt, expires, InvocationApprovalState.Pending);
    }

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            write(writer);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}
