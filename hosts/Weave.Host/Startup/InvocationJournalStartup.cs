using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Startup;

/// <summary>Resolve mandatory recording and authority before accepting work.</summary>
internal sealed class InvocationJournalStartup(IInvocationJournal journal, ICapabilityTokenService tokens) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
