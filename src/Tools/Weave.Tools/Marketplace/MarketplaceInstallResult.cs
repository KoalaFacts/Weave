using Weave.Workspaces.Templates;
namespace Weave.Tools.Marketplace;

/// <summary>
/// Outcome of a successful <c>marketplace:install</c>. Carries the resolved
/// <see cref="CapabilityTemplate"/> so the caller can compose a workspace from
/// it, plus the post-install counters off the marketplace item.
/// </summary>
public sealed record MarketplaceInstallResult
{
    public required MarketplaceItem Item { get; init; }
    public required CapabilityTemplate Template { get; init; }
}
