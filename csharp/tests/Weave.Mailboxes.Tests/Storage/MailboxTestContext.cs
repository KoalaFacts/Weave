using System.Text;
using Microsoft.Data.Sqlite;
using Weave.Contacts;
using Weave.Mailboxes.Sqlite;

namespace Weave.Mailboxes.Tests.Storage;

internal sealed class MailboxTestContext : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "weave-mailboxes-" + Guid.NewGuid());
    public MailboxTestContext(Func<MailboxOptions, MailboxOptions>? configure = null)
    {
        Directory.CreateDirectory(_directory);
        var options = new MailboxOptions { DatabasePath = Path.Combine(_directory, "mailboxes.db") };
        Options = configure is null ? options : configure(options);
        Store = Restart();
    }
    public MailboxOptions Options { get; }
    public MailboxTestClock Clock { get; } = new();
    public SqliteMailboxStore Store { get; }
    public static MailboxAuthority Alice { get; } = new(new("alice"));
    public static MailboxAuthority Bob { get; } = new(new("bob"));
    public static MailboxAuthority Eve { get; } = new(new("eve"));
    public SqliteMailboxStore Restart() => new(Options, Clock);
    public MailboxPayload Payload(string text = "private body", TimeSpan? lifetime = null)
    {
        var now = Clock.GetUtcNow();
        return new(Guid.CreateVersion7(now), now, now + (lifetime ?? TimeSpan.FromMinutes(20)),
            "application/octet-stream", Encoding.UTF8.GetBytes(text));
    }
    public ContactCard Card(string id = "bob-public", ContactVisibility visibility = ContactVisibility.Public,
        MailboxAuthority? owner = null) => new(new(id), (owner ?? Bob).MailboxId, visibility, Clock.GetUtcNow(), null,
        [new("relay", 1, "relay", "https://relay.example.test", "opaque handshake")]);
    public ContactRequestSubmission Submission(ContactCard? card = null, MailboxPayload? payload = null) =>
        new(new(Guid.CreateVersion7(Clock.GetUtcNow()).ToString()), (card ?? Card()).CardId, "relay", payload ?? Payload());
    public ContactRelation Request(ContactCard? card = null, MailboxAuthority? requester = null)
    {
        card ??= Card();
        Store.PutCard(new(card.OwnerMailboxId), card, TestContext.Current.CancellationToken).IsSuccess.ShouldBeTrue();
        return Require(Store.RequestContact(requester ?? Alice, Submission(card), TestContext.Current.CancellationToken));
    }
    public ContactRelation Connect()
    {
        var request = Request();
        var connected = Require(Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken));
        Store.Acknowledge(Bob, Alice.MailboxId, request.Request.RequestMessageId!.Value, TestContext.Current.CancellationToken).IsSuccess.ShouldBeTrue();
        return connected;
    }
    public MailboxEnvelope Envelope(ContactRelation relation, MailboxPayload? payload = null,
        MailboxAuthority? recipient = null) => new(1, (recipient ?? Bob).MailboxId, relation.Generation, payload ?? Payload());
    public static T Require<T>(MailboxResult<T> result) where T : class
    {
        result.Error.ShouldBeNull();
        return result.Value.ShouldNotBeNull();
    }
    public long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
