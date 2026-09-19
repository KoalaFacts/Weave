using System.Security.Cryptography;
using System.Text.Json;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Tools.Tool;

namespace Weave.Invocations;

internal static class InvocationFingerprint
{
    // Bound the additional hashing allocation. This is not an approved-plan format.
    private const int MaxInputCharacters = 1_048_576;

    public static InvocationRecord? Prepare(ToolInvocation request, CapabilityToken token,
        string connectorType, DateTimeOffset now)
    {
        var id = request.InvocationId ?? InvocationId.From(Guid.NewGuid().ToString("N"));
        if (!Guid.TryParseExact(id.ToString(), "N", out var guid) || guid == Guid.Empty)
            return null;
        id = InvocationId.From(guid.ToString("N"));
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
            // Versioned, ordered JSON fields; parameter ordering is ordinal. Null and
            // empty raw input remain distinct. Hash the owned, pre-substitution input.
            writer.WriteStartArray();
            writer.WriteNumberValue(1);
            writer.WriteStringValue(token.WorkspaceId);
            writer.WriteStringValue(token.IssuedTo);
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
        var digest = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
        return new InvocationRecord(id, token.WorkspaceId, token.IssuedTo, request.ToolName,
            request.Method, "v1:" + digest, token.TokenId, ToolCapability.Invoke(request.ToolName, request.Method), now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero));
    }
}
