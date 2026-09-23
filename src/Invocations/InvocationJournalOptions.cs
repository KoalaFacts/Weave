namespace Weave.Invocations;

public sealed class InvocationJournalOptions
{
    public const string ConfigurationSectionName = "Weave:Invocations";

    /// <summary>On-disk SQLite file. Null selects ~/.weave/invocations.db.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Reject missing or incompatible storage instead of initializing it at startup.</summary>
    public bool RequireExistingStorage { get; set; }

    public List<string> ApprovalRequiredGrants { get; set; } = [];
    public TimeSpan ApprovalLifetime { get; set; } = TimeSpan.FromMinutes(30);
}
