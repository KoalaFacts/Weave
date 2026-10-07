using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal sealed class MailboxEndpoints(IExpiringMailboxStore store, InboxEventStream stream)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/v1/messages", SendAsync);
        app.MapGet("/v1/inbox", InboxAsync);
        app.MapGet("/v1/inbox/events", stream.RunAsync);
        app.MapGet("/v1/outbox", OutboxAsync);
        app.MapGet("/v1/outbox/{messageId}", ReceiptAsync);
        app.MapPost("/v1/inbox/{messageId}/ack", AckAsync);
    }
    private async Task SendAsync(HttpContext context)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.MessageSubmissionWire);
        if (wire is null) return;
        if (wire.Payload.Bytes.Length > MailboxPayload.MaximumBytes) { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        var envelope = new MailboxEnvelope(wire.Version, new(wire.RecipientMailboxId), wire.ContactGeneration, MailboxWireMapping.Payload(wire.Payload));
        await MailboxHttp.ResultAsync(context, store.Send(MailboxHttp.Authority(context), envelope, context.RequestAborted),
            MailboxWireMapping.Receipt, MailboxJsonContext.Default.ReceiptWire);
    }
    private Task InboxAsync(HttpContext context)
    {
        var authority = MailboxHttp.Authority(context);
        if (!MailboxHttp.Page(context, "inbox", authority, out var cursor, out var limit)) return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var page = store.ReadPending(authority, cursor, limit, context.RequestAborted);
        return MailboxHttp.WriteAsync(context, new MailboxPageWire<ReceivedMessageWire>(page.Items.Select(MailboxWireMapping.Message).ToArray(), page.NextCursor),
            MailboxJsonContext.Default.MailboxPageWireReceivedMessageWire);
    }
    private Task OutboxAsync(HttpContext context)
    {
        var authority = MailboxHttp.Authority(context);
        if (!MailboxHttp.Page(context, "receipts", authority, out var cursor, out var limit)) return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var page = store.ListReceipts(authority, cursor, limit, context.RequestAborted);
        return MailboxHttp.WriteAsync(context, new MailboxPageWire<ReceiptWire>(page.Items.Select(MailboxWireMapping.Receipt).ToArray(), page.NextCursor),
            MailboxJsonContext.Default.MailboxPageWireReceiptWire);
    }
    private Task ReceiptAsync(HttpContext context)
    {
        if (!MailboxHttp.UuidRoute(context, "messageId", out var id)) return MailboxHttp.ErrorAsync(context, 400, "invalid");
        var receipt = store.GetReceipt(MailboxHttp.Authority(context), id, context.RequestAborted);
        return receipt is null ? MailboxHttp.ErrorAsync(context, 404, "unavailable")
            : MailboxHttp.WriteAsync(context, MailboxWireMapping.Receipt(receipt), MailboxJsonContext.Default.ReceiptWire);
    }
    private async Task AckAsync(HttpContext context)
    {
        var wire = await MailboxHttp.ReadAsync(context, MailboxJsonContext.Default.AckSubmissionWire);
        if (wire is null) return;
        if (!MailboxHttp.ValidIdentity(wire.SenderMailboxId) || !MailboxHttp.UuidRoute(context, "messageId", out var id)) { await MailboxHttp.ErrorAsync(context, 400, "invalid"); return; }
        await MailboxHttp.ResultAsync(context, store.Acknowledge(MailboxHttp.Authority(context), new(wire.SenderMailboxId), id, context.RequestAborted),
            MailboxWireMapping.Receipt, MailboxJsonContext.Default.ReceiptWire);
    }
}
