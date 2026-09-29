namespace Weave.Silo.Plugins;

public sealed record InstallationObservation(
    DateTimeOffset CheckedAt,
    InstallationFailureCode Failure);

public enum InstallationFailureCode
{
    None,
    StoredConfigurationInvalid,
    UnsupportedAuthorityState,
    DefinitionRevisionChanged,
    ContractUnpinned,
    InvalidConfiguration,
    RegistrationConflict,
    UnsupportedPluginType,
    PeerUnavailable,
    ContractRejected,
    ConnectionFailed,
    DependencyUnavailable,
    DependencyInUse
}
