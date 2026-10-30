namespace Weave.Mailboxes;

public sealed record MailboxOptions
{
    public required string DatabasePath { get; init; }
    public bool RequireExistingStorage { get; init; }
    public int MaximumMailboxes { get; init; } = 100;
    public int MaximumPendingMessagesPerMailbox { get; init; } = 64;
    public long MaximumPendingBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumMessageRows { get; init; } = 8000;
    public int MaximumPendingRequestsPerRecipient { get; init; } = 32;
    public int MaximumRequestRows { get; init; } = 1000;
    public int MaximumChannels { get; init; } = 5000;
    public int MaximumCards { get; init; } = 1000;
    public TimeSpan MaximumMessageLifetime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan FirstAdmissionWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FutureClockSkew { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TerminalRetention { get; init; } = TimeSpan.FromDays(7);
}
