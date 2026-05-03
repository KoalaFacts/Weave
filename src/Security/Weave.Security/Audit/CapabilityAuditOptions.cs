namespace Weave.Security.Audit;

public sealed class CapabilityAuditOptions
{
    public const string ConfigurationSectionName = "CapabilityAudit";

    /// <summary>
    /// Maximum number of events held in the in-memory store. When exceeded,
    /// the oldest event is evicted. Default 10,000.
    /// </summary>
    public int Capacity { get; init; } = 10_000;
}
