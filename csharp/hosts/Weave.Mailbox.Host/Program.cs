using System.Collections.Immutable;
using Weave.Mailboxes;
using Weave.Mailboxes.Sqlite;

namespace Weave.Mailbox.Host;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        var section = configuration.GetSection("Mailbox");
        var controls = section.GetSection("Controls").GetChildren().Select(entry => new MailboxControlCredential(
            entry["MailboxId"] ?? throw new InvalidOperationException("Mailbox control owner is required."),
            entry["Sha256"] ?? throw new InvalidOperationException("Mailbox control hash is required."))).ToImmutableArray();
        var storage = new MailboxOptions
        {
            DatabasePath = section["DatabasePath"] ?? throw new InvalidOperationException("Mailbox database path is required."),
            RequireExistingStorage = !string.Equals(section["AllowCreateStorage"], "true", StringComparison.Ordinal)
        };
        var clock = TimeProvider.System;
        var options = new MailboxHostOptions { Credentials = controls };
        await using var host = MailboxHost.Create(options, new SqliteMailboxStore(storage, clock), clock, new(),
            builder => builder.Configuration.AddConfiguration(configuration));
        await host.RunAsync();
    }
}
