using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
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

    private static VirtualActorId GetProofVerifierActorId(object workspaceId) =>
        VirtualActorId.From(workspaceId.ToString()!);

    public ValueTask EnqueueAsync(AgentVerificationRequest request, CancellationToken ct) =>
        _channel.Writer.WriteAsync(request, ct);

    public async Task ProcessAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var verifier = _actors.GetActor<IProofVerifierActor>(
                    GetProofVerifierActorId(request.WorkspaceId));

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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
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
