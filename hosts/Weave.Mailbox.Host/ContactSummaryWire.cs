namespace Weave.Mailbox.Host;

public sealed record ContactSummaryWire(string RequestId, string CardId, string RequesterMailboxId, string RecipientMailboxId, string MethodId, long Generation, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? RequestMessageId, Guid? ReplyMessageId);
