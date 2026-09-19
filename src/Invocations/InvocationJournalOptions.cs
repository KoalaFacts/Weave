namespace Weave.Invocations;

public sealed class InvocationJournalOptions
{
    public const string ConfigurationSectionName = "Weave:Invocations";

    /// <summary>On-disk SQLite file. Null selects ~/.weave/invocations.db.</summary>
    public string? DatabasePath { get; set; }

    public List<string> ApprovalRequiredGrants { get; set; } = [];
    public TimeSpan ApprovalLifetime { get; set; } = TimeSpan.FromMinutes(30);
}
