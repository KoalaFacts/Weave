namespace Weave.Mailboxes;

/// <summary>Trusted mailbox-control scope constructed by the authenticated host, never from request data.</summary>
public sealed record MailboxAuthority(MailboxId MailboxId);
