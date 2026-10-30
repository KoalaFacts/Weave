using Weave.Management;

namespace Weave.Silo.Startup;

internal sealed class ManagementJournalStartup(IManagementOperationJournal journal) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
