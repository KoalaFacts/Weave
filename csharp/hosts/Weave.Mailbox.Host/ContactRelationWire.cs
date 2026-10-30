namespace Weave.Mailbox.Host;

public sealed record ContactRelationWire(ContactSummaryWire Request, long Generation, bool IsBlocked, bool IsConnected, DateTimeOffset UpdatedAt);
