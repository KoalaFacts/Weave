namespace Weave.Invocations;

public sealed class InvocationJournalOptions
{
    public const string ConfigurationSectionName = "Weave:Invocations";

    /// <summary>On-disk SQLite file. Null selects ~/.weave/invocations.db.</summary>
    public string? DatabasePath { get; set; }
}
