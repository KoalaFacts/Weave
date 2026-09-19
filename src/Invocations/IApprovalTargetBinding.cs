using Weave.Tools.Tool;

namespace Weave.Invocations;

/// <summary>
/// Optional trusted-adapter contribution. Return a versioned digest of the actual
/// registered target/configuration, stable across equivalent reconnects, or null.
/// Never return a caller-supplied URL or a display name as target authority.
/// </summary>
public interface IApprovalTargetBinding
{
    string? GetApprovalTargetDigest(ToolHandle handle);

    /// <summary>Plain-text description of that registered target, or null when review is unsupported.</summary>
    string? GetApprovalTargetDescription(ToolHandle handle) => null;
}
