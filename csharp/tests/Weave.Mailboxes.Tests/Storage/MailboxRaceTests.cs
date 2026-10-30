using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxRaceTests
{
    [Fact]
    public async Task Send_ConcurrentIdenticalConnections_AdmitOnePayload()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var stores = Enumerable.Range(0, 8).Select(_ => context.Restart()).ToArray();
        var envelope = context.Envelope(relation);
        using var start = new ManualResetEventSlim();
        var tasks = stores.Select(store => Task.Run(() => { start.Wait(TestContext.Current.CancellationToken); return store.Send(Alice, envelope, TestContext.Current.CancellationToken); })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        results.ShouldAllBe(result => result.IsSuccess);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
    }

    [Fact]
    public async Task Send_ConcurrentQuotaBoundary_OnlyOneAdmissionWins()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingMessagesPerMailbox = 1 });
        var relation = context.Connect();
        var other = context.Restart();
        using var start = new ManualResetEventSlim();
        var tasks = new[] { context.Store, other }.Select(store => Task.Run(() =>
        { start.Wait(TestContext.Current.CancellationToken); return store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken); })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        results.Count(x => x.IsSuccess).ShouldBe(1);
        results.Count(x => x.Error == MailboxError.Capacity).ShouldBe(1);
    }

    [Fact]
    public async Task Acknowledge_ConcurrentExpiryAndBlock_NeverRestoresPayload()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation, context.Payload(lifetime: TimeSpan.FromSeconds(1)));
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var stores = new[] { context.Store, context.Restart(), context.Restart() };
        await Task.WhenAll(
            Task.Run(() => Require(stores[0].Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken))),
            Task.Run(() => stores[1].Sweep(100, TestContext.Current.CancellationToken)),
            Task.Run(() => Require(stores[2].SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, TestContext.Current.CancellationToken))));
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken)!.State.ShouldBe(MailboxReceiptState.Expired);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }
    [Fact]
    public async Task Acknowledge_LiveBlockRace_StableTerminalAndOneQuotaRelease()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingMessagesPerMailbox = 1 });
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        using var start = new ManualResetEventSlim();
        var other = context.Restart();
        var ack = Task.Run(() => { start.Wait(TestContext.Current.CancellationToken); return Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken)); });
        var block = Task.Run(() => { start.Wait(TestContext.Current.CancellationToken); return Require(other.SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, TestContext.Current.CancellationToken)); });
        start.Set();
        await Task.WhenAll(ack, block);
        var receipt = await ack;
        receipt.State.ShouldBeOneOf(MailboxReceiptState.Acknowledged, MailboxReceiptState.Blocked);
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBe(receipt);
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken)).ShouldBe(receipt);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
        context.Scalar("SELECT COALESCE(sum(payload_size),0) FROM mailbox_messages").ShouldBe(0);
        Require(context.Store.SetBlocked(Bob, Alice.MailboxId, (await block).Generation, false, TestContext.Current.CancellationToken));
        var renewed = context.Request();
        var connected = Require(context.Store.DecideContact(Bob, new(renewed.Request.Locator, renewed.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken));
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, renewed.Request.RequestMessageId!.Value, TestContext.Current.CancellationToken));
        Require(context.Store.Send(Alice, context.Envelope(connected), TestContext.Current.CancellationToken));
        context.Store.Send(Alice, context.Envelope(connected), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
    }
}
