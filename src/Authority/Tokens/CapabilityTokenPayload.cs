using System.Text;

namespace Weave.Security.Tokens;

internal static class CapabilityTokenPayload
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumFieldLength = 1024;
    private const int MaximumGrantCount = 256;

    public static bool HasValidTokenId(string? tokenId) =>
        tokenId is { Length: 32 } && tokenId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool HasValidFields(CapabilityToken? token) =>
        token is not null
        && HasValidTokenId(token.TokenId)
        && IsValidField(token.WorkspaceId)
        && IsValidField(token.IssuedTo)
        && token.Grants is not null
        && token.Grants.Count <= MaximumGrantCount
        && token.Grants.All(IsValidField);

    public static byte[] Encode(CapabilityToken token)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, StrictUtf8, leaveOpen: true);
        // Domain separation intentionally invalidates the ambiguous pre-v2 signing format.
        writer.Write("Weave.CapabilityToken/v2");
        writer.Write(token.TokenId);
        writer.Write(token.WorkspaceId);
        writer.Write(token.IssuedTo);
        writer.Write(token.IssuedAt.UtcDateTime.Ticks);
        writer.Write(token.ExpiresAt.UtcDateTime.Ticks);
        writer.Write(token.Grants.Count);
        foreach (var grant in token.Grants.Order(StringComparer.Ordinal))
            writer.Write(grant);
        writer.Flush();
        return buffer.ToArray();
    }

    private static bool IsValidField(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumFieldLength;
}
