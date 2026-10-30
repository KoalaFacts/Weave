using System.Security.Cryptography;
using System.Text.Json;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Tools.Tool;

namespace Weave.Invocations;

public static class InvocationFingerprint
{
    private const int MaxInputCharacters = 1_048_576;

    internal static InvocationRecord? Prepare(ToolInvocation request, CapabilityToken token,
        string connectorType, DateTimeOffset now)
    {
        var id = request.InvocationId ?? InvocationId.From(Guid.NewGuid().ToString("N"));
        if (!Guid.TryParseExact(id.ToString(), "N", out var guid) || guid == Guid.Empty)
            return null;
        var digest = ComputeInputDigest(request, token.WorkspaceId, token.IssuedTo, connectorType);
        if (digest is null)
            return null;
        return new InvocationRecord(InvocationId.From(guid.ToString("N")), token.WorkspaceId, token.IssuedTo,
            request.ToolName, request.Method, digest, token.TokenId,
            ToolCapability.Invoke(request.ToolName, request.Method), now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero));
    }

    // Pure binding calculation. The supplied identity is evidence to compare, never authority.
    public static string? ComputeInputDigest(ToolInvocation request, string workspaceId, string subject, string connectorType)
    {
        long length = request.RawInput?.Length ?? 0;
        foreach (var pair in request.Parameters)
        {
            if (pair.Value is null)
                return null;
            length += (long)pair.Key.Length + pair.Value.Length;
        }
        if (length > MaxInputCharacters)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Preserve the existing v1 encoding exactly, including null versus empty input.
            writer.WriteStartArray();
            writer.WriteNumberValue(1);
            writer.WriteStringValue(workspaceId);
            writer.WriteStringValue(subject);
            writer.WriteStringValue(request.ToolName);
            writer.WriteStringValue(connectorType);
            writer.WriteStringValue(request.Method);
            writer.WriteStartArray();
            foreach (var pair in request.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(pair.Key);
                writer.WriteStringValue(pair.Value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStringValue(request.RawInput);
            writer.WriteEndArray();
        }
        return "v1:" + Convert.ToHexString(SHA256.HashData(
            buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}
