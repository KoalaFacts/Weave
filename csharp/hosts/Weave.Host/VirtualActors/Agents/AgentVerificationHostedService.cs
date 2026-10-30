using Microsoft.Extensions.Hosting;
using Weave.Agents.Verification;

namespace Weave.Silo.VirtualActors;

public sealed class AgentVerificationHostedService(AgentVerificationDispatcher dispatcher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        dispatcher.ProcessAsync(stoppingToken);
}
