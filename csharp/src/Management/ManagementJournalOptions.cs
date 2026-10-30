namespace Weave.Management;

public sealed class ManagementJournalOptions
{
    public const string ConfigurationSectionName = "Weave:ManagementJournal";

    public string? DatabasePath { get; set; }
    public bool RequireExistingStorage { get; set; }
}
