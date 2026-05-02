using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Verification;

public sealed class AgentVerificationDispatcher : IAgentVerificationDispatcher
{
    private readonly Channel<AgentVerificationRequest> _channel =
        Channel.CreateUnbounded<AgentVerificationRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly IVirtualActorProvider _actors;
    private readonly ILogger<AgentVerificationDispatcher> _logger;

    public AgentVerificationDispatcher(
        IVirtualActorProvider actors,
        ILogger<AgentVerificationDispatcher> logger)
    {
        _actors = actors;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(AgentVerificationRequest request, CancellationToken ct) =>
        _channel.Writer.WriteAsync(request, ct);

    public async Task ProcessAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var verifier = _actors.GetActor<IProofVerifierActor>(
                    VirtualActorId.From(request.WorkspaceId.ToString()));

                await verifier.VerifyAsync(
                    request.WorkspaceId,
                    request.AgentName,
                    request.TaskId,
                    request.Proof);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Proof verification failed for task {TaskId} on agent {AgentName}",
                    request.TaskId,
                    request.AgentName);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Proof verification failed for task {TaskId} on agent {AgentName}",
                    request.TaskId,
                    request.AgentName);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Proof verification timed out for task {TaskId} on agent {AgentName}",
                    request.TaskId,
                    request.AgentName);
            }
        }
    }
}
