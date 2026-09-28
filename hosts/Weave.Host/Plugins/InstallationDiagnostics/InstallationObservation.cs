namespace Weave.Silo.Plugins;

public sealed record InstallationObservation(
    DateTimeOffset CheckedAt,
    InstallationFailureCode Failure);

public enum InstallationFailureCode
{
    None,
    StoredConfigurationInvalid,
    ContractUnpinned,
    InvalidConfiguration,
    RegistrationConflict,
    UnsupportedPluginType,
    PeerUnavailable,
    ContractRejected,
    ConnectionFailed
}
