using Weave.Mailboxes;

namespace Weave.Contacts;

/// <summary>Identity and generation are resolved inside the admission transaction.</summary>
public sealed record ContactRequestSubmission(ContactRequestId RequestId, ContactCardId CardId,
    string MethodId, MailboxPayload Payload);
