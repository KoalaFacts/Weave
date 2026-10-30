namespace Weave.Plugins;

public abstract record PluginInstallation
{
    public string Id { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public string DefinitionRevision { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public bool DesiredEnabled { get; set; }
    public List<string> RequestedPermissions { get; init; } = [];
    public List<string> GrantedPermissions { get; init; } = [];
    public List<string> CredentialReferences { get; init; } = [];

    public bool HasUnsupportedAuthority() => RequestedPermissions.Count != 0
        || GrantedPermissions.Count != 0 || CredentialReferences.Count != 0;
}
