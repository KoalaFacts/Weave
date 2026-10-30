namespace Weave.Silo.Plugins;

public sealed record InstallationProbeObservation(
    DateTimeOffset CheckedAt,
    InstallationFailureCode Failure,
    string DefinitionRevision,
    string ConfigDigest,
    string ContractDigest);
