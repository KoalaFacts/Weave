using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactDecision(ContactRequestLocator Request, long ExpectedGeneration, ContactStatus Status,
    MailboxPayload? Reply = null);
