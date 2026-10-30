namespace Weave.Silo.Api;

public sealed record PluginInstallationResponse(
    string Id,
    string PluginName,
    string Type,
    string DefinitionRevision,
    bool DesiredEnabled,
    bool RuntimeConnected,
    string Condition,
    string? ReasonCode,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<string> RequestedPermissions,
    IReadOnlyList<string> GrantedPermissions,
    bool HasCredentialReferences,
    string ProbeCondition,
    string? ProbeReasonCode,
    DateTimeOffset? ProbeCheckedAt);
