using Microsoft.Data.Sqlite;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal sealed partial class MailboxCleanupService(IExpiringMailboxStore store, MailboxHostOptions options,
    TimeProvider timeProvider, ILogger<MailboxCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.CleanupInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { store.Sweep(options.CleanupBatchSize, stoppingToken); }
            catch (SqliteException) { StorageUnavailable(logger); }
        }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox cleanup could not access storage.")]
    private static partial void StorageUnavailable(ILogger logger);
}
