namespace Weave.Security.Audit;

public sealed class CapabilityAuditOptions
{
    public const string ConfigurationSectionName = "CapabilityAudit";

    public const string MemoryBackend = "memory";
    public const string SqliteBackend = "sqlite";
    public const string PostgreSqlBackend = "postgresql";

    /// <summary>
    /// Maximum number of events held by the audit store. When exceeded the
    /// oldest event is evicted (FIFO). Applies to both the in-memory and
    /// SQLite-backed stores. Default 10,000.
    /// </summary>
    public int Capacity { get; init; } = 10_000;

    /// <summary>
    /// Selects the <see cref="ICapabilityAuditStore"/> implementation. One of:
    /// <c>"memory"</c> (default — in-process, evicts on silo restart);
    /// <c>"sqlite"</c> (persists to a SQLite file under <c>~/.weave/</c>; single-silo);
    /// <c>"postgresql"</c> (shared backend for multi-silo deployments;
    /// <see cref="ConnectionString"/> required).
    /// </summary>
    public string Backend { get; init; } = MemoryBackend;

    /// <summary>
    /// Connection string for backends that need one. Required for
    /// <c>"postgresql"</c>. Optional for <c>"sqlite"</c> (defaults to
    /// <c>~/.weave/audit.db</c>; set to <c>"Data Source=:memory:"</c> for
    /// ephemeral in-process SQLite). Ignored for <c>"memory"</c>.
    /// </summary>
    public string? ConnectionString { get; init; }
}
