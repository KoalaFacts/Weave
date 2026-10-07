using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactDecision(ContactRequestId RequestId, long ExpectedGeneration, ContactStatus Status,
    MailboxPayload? Reply = null);
