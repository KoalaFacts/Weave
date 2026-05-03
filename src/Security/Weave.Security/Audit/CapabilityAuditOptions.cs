namespace Weave.Security.Audit;

public sealed class CapabilityAuditOptions
{
    public const string ConfigurationSectionName = "CapabilityAudit";

    public const string MemoryBackend = "memory";
    public const string SqliteBackend = "sqlite";

    /// <summary>
    /// Maximum number of events held by the audit store. When exceeded the
    /// oldest event is evicted (FIFO). Applies to both the in-memory and
    /// SQLite-backed stores. Default 10,000.
    /// </summary>
    public int Capacity { get; init; } = 10_000;

    /// <summary>
    /// Selects the <see cref="ICapabilityAuditStore"/> implementation. One of
    /// <c>"memory"</c> (default — in-process, evicts on silo restart) or
    /// <c>"sqlite"</c> (persists to a SQLite file under <c>~/.weave/</c>;
    /// connection string overridable via <see cref="ConnectionString"/>).
    /// </summary>
    public string Backend { get; init; } = MemoryBackend;

    /// <summary>
    /// Optional connection string for backends that need one. Today only
    /// <c>"sqlite"</c> uses it; if blank, a default file under
    /// <c>~/.weave/audit.db</c> is used. Set to <c>"Data Source=:memory:"</c>
    /// for ephemeral in-process SQLite (tests).
    /// </summary>
    public string? ConnectionString { get; init; }
}
