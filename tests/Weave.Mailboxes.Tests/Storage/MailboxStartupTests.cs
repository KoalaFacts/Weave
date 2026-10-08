using Weave.Mailboxes.Sqlite;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxStartupTests
{
    [Fact]
    public async Task Initialize_ConcurrentFirstOpen_ProducesOneUsableSchema()
    {
        using var context = new MailboxTestContext();
        var options = context.Options with { DatabasePath = context.Options.DatabasePath + ".concurrent" };
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return new SqliteMailboxStore(options, context.Clock);
        })).ToArray();
        start.Set();
        var stores = await Task.WhenAll(tasks);
        Require(stores[0].PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        stores.ShouldAllBe(store => store.FindCard(context.Card().CardId, Alice, TestContext.Current.CancellationToken) != null);
    }
}
