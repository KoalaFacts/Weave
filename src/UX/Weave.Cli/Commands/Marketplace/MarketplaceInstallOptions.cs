namespace Weave.Cli.Commands;

internal sealed record MarketplaceInstallOptions(string? ItemId, bool NoScaffold = false, string? WorkspaceName = null);
