using System.Text.Json.Serialization;
using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

[JsonSerializable(typeof(ContactMethod[]))]
internal sealed partial class MailboxCardJsonContext : JsonSerializerContext;
