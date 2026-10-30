using System.Collections.Immutable;

namespace Weave.Mailbox.Host;

public sealed record MailboxHostOptions
{
    public ImmutableArray<MailboxControlCredential> Credentials { get; init; } = [];
    public TimeSpan StreamLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StreamWriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumStreamsPerMailbox { get; init; } = 2;
    public int MaximumStreams { get; init; } = 100;
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(1);
    public int CleanupBatchSize { get; init; } = 100;
}
