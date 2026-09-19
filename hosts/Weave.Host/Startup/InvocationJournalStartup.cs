using Weave.Invocations;

namespace Weave.Silo.Startup;

/// <summary>Resolve the mandatory journal before accepting work; missing/broken storage blocks startup.</summary>
internal sealed class InvocationJournalStartup(IInvocationJournal journal) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
