using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

internal sealed record MailboxChannelRow(string Low, string High, long Generation, bool BlockedLow,
    bool BlockedHigh, string? CurrentRequest, string? CurrentRequester, DateTimeOffset UpdatedAt)
{
    public bool IsBlocked => BlockedLow || BlockedHigh;
    public ContactChannelSnapshot Snapshot(MailboxId viewer) => new(viewer,
        new(viewer.Value == Low ? High : Low), Generation, viewer.Value == Low ? BlockedLow : BlockedHigh,
        viewer.Value == Low ? BlockedHigh : BlockedLow, CurrentRequest is null ? null : new(new(CurrentRequester!), new(CurrentRequest)));
    public ContactRelation Relation(ContactRequestSummary request, DateTimeOffset updated) => new(request, IsBlocked, updated > UpdatedAt ? updated : UpdatedAt)
    {
        Generation = Generation,
        IsConnected = CurrentRequester == request.RequesterMailboxId.Value && CurrentRequest == request.RequestId.Value && request.Generation == Generation
            && request.Status == ContactStatus.Accepted && !IsBlocked
    };
}
