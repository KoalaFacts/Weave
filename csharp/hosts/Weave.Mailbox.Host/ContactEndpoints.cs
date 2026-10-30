using Weave.Contacts;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal sealed class ContactEndpoints(IExpiringMailboxStore store)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/v1/contacts/cards", context => CardsAsync(context, false)).WithMetadata(new PublicCardDiscovery());
        app.MapGet("/v1/contacts/owned-cards", context => CardsAsync(context, true));
        app.MapGet("/v1/contacts/card", FindCardAsync).WithMetadata(new PublicCardDiscovery());
        app.MapPut("/v1/contacts/cards", PutCardAsync);
        app.MapGet("/v1/contacts/requests", RequestsAsync);
        app.MapGet("/v1/contacts/requests/{requestId}", RequestAsync);
        app.MapPost("/v1/contacts/requests", SubmitAsync);
        app.MapPost("/v1/contacts/requests/{requestId}/decision", DecideAsync);
        app.MapGet("/v1/contacts/channel", ChannelAsync);
        app.MapPut("/v1/contacts/blocks", context => BlockAsync(context, true));
        app.MapDelete("/v1/contacts/blocks", context => BlockAsync(context, false));
    }
    private Task CardsAsync(HttpContext context, bool owned)
    {
        var owner = owned ? MailboxHttp.Authority(context) : null;
        var scope = owner ?? new MailboxAuthority(new("public"));
        if (!MailboxHttp.Page(context, owned ? "owned-cards" : "public-cards", scope, out var cursor, out var limit))
            return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var page = store.ListCards(owner, cursor, limit, context.RequestAborted);
        return MailboxHttp.WriteAsync(context, new MailboxPageWire<CardWire>(page.Items.Select(MailboxWireMapping.Card).ToArray(), page.NextCursor),
            MailboxJsonContext.Default.MailboxPageWireCardWire);
    }
    private Task FindCardAsync(HttpContext context)
    {
        if (!MailboxHttp.IdentityQuery(context, "cardId", out var cardId))
            return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var card = store.FindCard(new(cardId), context.Features.Get<MailboxControlScope>()?.Authority, context.RequestAborted);
        return card is null ? MailboxHttp.ErrorAsync(context, 404, "unavailable")
            : MailboxHttp.WriteAsync(context, MailboxWireMapping.Card(card), MailboxJsonContext.Default.CardWire);
    }
    private async Task PutCardAsync(HttpContext context)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.CardSubmissionWire);
        if (wire is null)
            return;
        var visibility = wire.Visibility switch { "public" => ContactVisibility.Public, "unlisted" => ContactVisibility.Unlisted, _ => (ContactVisibility?)null };
        if (visibility is null || wire.Methods.Length is < 1 or > ContactCardPolicy.MaximumMethods || wire.Methods.Any(m => m is null))
        { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        var authority = MailboxHttp.Authority(context);
        var card = new ContactCard(new(wire.CardId), authority.MailboxId, visibility.Value, wire.CreatedAt, wire.ExpiresAt,
            wire.Methods.Select(m => new ContactMethod(m.MethodId, m.Version, m.Transport, m.Endpoint, m.Instructions)))
        { RevokedAt = wire.RevokedAt, AudienceHint = wire.AudienceHint };
        await MailboxHttp.ResultAsync(context, store.PutCard(authority, card, context.RequestAborted), MailboxWireMapping.Card, MailboxJsonContext.Default.CardWire);
    }
    private Task RequestsAsync(HttpContext context)
    {
        var authority = MailboxHttp.Authority(context);
        if (!MailboxHttp.Page(context, "contacts", authority, out var cursor, out var limit))
            return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var page = store.ListContactRequests(authority, cursor, limit, context.RequestAborted);
        return MailboxHttp.WriteAsync(context, new MailboxPageWire<ContactSummaryWire>(page.Items.Select(MailboxWireMapping.Summary).ToArray(), page.NextCursor),
            MailboxJsonContext.Default.MailboxPageWireContactSummaryWire);
    }
    private Task RequestAsync(HttpContext context)
    {
        if (!MailboxHttp.IdentityQuery(context, "requesterMailboxId", out var requester)
            || !MailboxHttp.UuidRoute(context, "requestId", out var id))
            return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var request = store.GetContactRequest(MailboxHttp.Authority(context), new(new(requester), new(id.ToString("D"))), context.RequestAborted);
        return request is null ? MailboxHttp.ErrorAsync(context, 404, "unavailable")
            : MailboxHttp.WriteAsync(context, MailboxWireMapping.Summary(request), MailboxJsonContext.Default.ContactSummaryWire);
    }
    private async Task SubmitAsync(HttpContext context)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.ContactSubmissionWire);
        if (wire is null)
            return;
        if (wire.Payload.Bytes.Length > MailboxPayload.MaximumBytes)
        { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        var submission = new ContactRequestSubmission(new(wire.RequestId), new(wire.CardId), wire.MethodId, MailboxWireMapping.Payload(wire.Payload));
        await MailboxHttp.ResultAsync(context, store.RequestContact(MailboxHttp.Authority(context), submission, context.RequestAborted),
            MailboxWireMapping.Relation, MailboxJsonContext.Default.ContactRelationWire);
    }
    private async Task DecideAsync(HttpContext context)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.ContactDecisionWire);
        if (wire is null)
            return;
        var status = MailboxWireMapping.Status(wire.Status);
        if (status is null || !MailboxHttp.ValidIdentity(wire.RequesterMailboxId) || !MailboxHttp.UuidRoute(context, "requestId", out var id) || wire.Reply?.Bytes.Length > MailboxPayload.MaximumBytes)
        { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        var decision = new ContactDecision(new(new(wire.RequesterMailboxId), new(id.ToString("D"))), wire.ExpectedGeneration, status.Value,
            wire.Reply is null ? null : MailboxWireMapping.Payload(wire.Reply));
        await MailboxHttp.ResultAsync(context, store.DecideContact(MailboxHttp.Authority(context), decision, context.RequestAborted),
            MailboxWireMapping.Relation, MailboxJsonContext.Default.ContactRelationWire);
    }
    private Task ChannelAsync(HttpContext context)
    {
        if (!MailboxHttp.IdentityQuery(context, "peerMailboxId", out var peer))
            return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var channel = store.GetContactChannel(MailboxHttp.Authority(context), new(peer), context.RequestAborted);
        return channel is null ? MailboxHttp.ErrorAsync(context, 404, "unavailable")
            : MailboxHttp.WriteAsync(context, MailboxWireMapping.Channel(channel), MailboxJsonContext.Default.ContactChannelWire);
    }
    private async Task BlockAsync(HttpContext context, bool blocked)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.BlockSubmissionWire);
        if (wire is null)
            return;
        if (!MailboxHttp.ValidIdentity(wire.PeerMailboxId))
        { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        await MailboxHttp.ResultAsync(context, store.SetBlocked(MailboxHttp.Authority(context), new(wire.PeerMailboxId),
            wire.ExpectedGeneration, blocked, context.RequestAborted), MailboxWireMapping.Channel, MailboxJsonContext.Default.ContactChannelWire);
    }
}
