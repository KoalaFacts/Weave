namespace Weave.Workspaces.Manifest;

public sealed record SecretsConfig
{
    public string Provider { get; init; } = "env";
    public VaultConfig? Vault { get; init; }
}
