using System.Security.Cryptography;
using System.Text;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Workspaces.Lifecycle;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed partial class WorkspaceRuntimeRecovery
{
    public const string ReconcileGrant = "workspace:runtime:reconcile";

    public async Task<WorkspaceRuntimeReconciliationResult> ReconcileAsync(WorkspaceState state,
        WorkspaceRuntimeReconciliationRequest request, CapabilityToken token, string managementId,
        Func<CancellationToken, Task> persist, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.WorkspaceId.IsEmpty)
            throw new UnauthorizedAccessException("Workspace identity is unavailable.");
        var owner = state.WorkspaceId.ToString();
        await authorizer.AuthorizeAsync(token, ReconcileGrant, owner);
        if (!Guid.TryParseExact(managementId, "N", out var id) || id == Guid.Empty
            || request.ExpectedResourceSetDigest is not { Length: 64 } digest
            || digest.Any(character => !char.IsAsciiHexDigit(character)))
            return new() { Outcome = WorkspaceRuntimeReconciliationOutcome.InvalidRequest };
        managementId = id.ToString("N");
        var services = await DescribeServicesAsync(state, ct);
        if (services is { BlockReason: null }
            && (request.ExpectedHostedServiceDigest is not { Length: 64 } serviceDigest
                || serviceDigest.Any(character => !char.IsAsciiHexDigit(character))))
            return new() { Outcome = WorkspaceRuntimeReconciliationOutcome.InvalidRequest };
        var grants = services is { BlockReason: null }
            ? services.RequiredGrants.Prepend(ReconcileGrant).Order(StringComparer.Ordinal).ToArray() : [ReconcileGrant];
        foreach (var grant in grants.Where(grant => grant != ReconcileGrant))
            await authorizer.AuthorizeAsync(token, grant, owner);
        if (!journal.TryAdmit(new ManagementOperationRecord
        {
            Id = managementId,
            WorkspaceId = owner,
            Subject = token.IssuedTo,
            TokenId = token.TokenId,
            Action = ReconcileGrant,
            Target = owner,
            AuthorizedGrants = string.Join(',', grants),
            RequestDigest = request.ExpectedHostedServiceDigest is null ? digest
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{digest}:{request.ExpectedHostedServiceDigest}"))),
            AdmittedAt = timeProvider.GetUtcNow()
        }, ct))
            return new() { Outcome = WorkspaceRuntimeReconciliationOutcome.AlreadyAdmitted };

        WorkspaceRuntimeReconciliationResult result;
        var serviceRestorationStarted = false;
        try
        {
            if (!string.Equals(digest, WorkspaceRuntimeResourceSet.Digest(state), StringComparison.Ordinal))
                result = new() { Outcome = WorkspaceRuntimeReconciliationOutcome.PlanChanged };
            else if (state.RecoveryCondition is not (WorkspaceRecoveryCondition.StartedOnThisHost
                or WorkspaceRecoveryCondition.RequiresReconciliation or WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost))
                result = new() { Reason = "recovery-condition-unconfirmed" };
            else if (services?.BlockReason is { } blockReason)
                result = new() { Reason = blockReason };
            else if (services is not null && services.Digest != request.ExpectedHostedServiceDigest)
                result = new() { Outcome = WorkspaceRuntimeReconciliationOutcome.PlanChanged };
            else if (runtime.InstanceId == Guid.Empty || state.Containers.Any(item => item.ContainerId.IsEmpty)
                || state.Containers.Select(item => item.ContainerId).Distinct().Count() != state.Containers.Count)
                result = new() { Reason = "invalid-runtime-identity" };
            else
            {
                var observed = await ObserveAuthorizedAsync(state, ct);
                // Evaluate resource observations against a proposed confirmation without changing owned state.
                var proposed = new WorkspaceState
                {
                    Status = state.Status,
                    RecoveryCondition = WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost
                };
                var readiness = WorkspaceRuntimeReadiness.Evaluate(proposed, true, observed.Network, observed.Containers);
                if (readiness.Condition is not WorkspaceRuntimeReadinessCondition.Ready)
                    result = new() { Reason = "runtime-resources-not-ready", Observation = observed };
                else
                {
                    if (!await PlanMatchesAsync())
                        result = new() { Outcome = WorkspaceRuntimeReconciliationOutcome.PlanChanged };
                    else
                    {
                        if (services is not null)
                        {
                            foreach (var grant in grants)
                                await authorizer.AuthorizeAsync(token, grant, owner);
                            ct.ThrowIfCancellationRequested();
                        }
                        serviceRestorationStarted = services is not null;
                        var restorationFailure = services is null ? null
                            : await hostedServices.RestoreAsync(CaptureServices(state), services.Digest, token, ct);
                        if (services is not null && restorationFailure is null)
                        {
                            foreach (var grant in grants)
                                await authorizer.AuthorizeAsync(token, grant, owner);
                            ct.ThrowIfCancellationRequested();
                            var serviceObservation = await hostedServices.ObserveAsync(CaptureServices(state), services.Digest, ct);
                            ct.ThrowIfCancellationRequested();
                            observed = await ObserveAuthorizedAsync(state, ct);
                            observed = observed with
                            {
                                HostedServiceObservation = serviceObservation,
                                Readiness = observed.Readiness.IncludeHostedServices(serviceObservation),
                                ObservedAt = timeProvider.GetUtcNow()
                            };
                            readiness = WorkspaceRuntimeReadiness.Evaluate(proposed, true, observed.Network, observed.Containers);
                        }
                        foreach (var grant in grants)
                            await authorizer.AuthorizeAsync(token, grant, owner);
                        ct.ThrowIfCancellationRequested();
                        if (!await PlanMatchesAsync())
                            result = new() { Outcome = WorkspaceRuntimeReconciliationOutcome.PlanChanged };
                        else if (restorationFailure is not null)
                            result = new() { Reason = restorationFailure, Observation = observed };
                        else if (readiness.Condition is not WorkspaceRuntimeReadinessCondition.Ready)
                            result = new() { Reason = "runtime-resources-not-ready", Observation = observed };
                        else if (services is not null && observed.HostedServiceObservation?.Condition is not WorkspaceRuntimeReadinessCondition.Ready)
                            result = new() { Reason = "hosted-services-not-ready", Observation = observed };
                        else
                        {
                            result = await ConfirmAsync(state, observed, readiness, persist);
                            result = result with
                            {
                                HostedServicesRestored = services is not null
                                && result.Outcome is WorkspaceRuntimeReconciliationOutcome.Confirmed
                            };
                        }
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (!serviceRestorationStarted)
                journal.Complete(managementId, ManagementOperationOutcome.Failed, timeProvider.GetUtcNow());
            throw;
        }
        if (result.Outcome is WorkspaceRuntimeReconciliationOutcome.OutcomeUnknown)
            return result;
        var outcome = result.Outcome is WorkspaceRuntimeReconciliationOutcome.Confirmed
            ? ManagementOperationOutcome.Succeeded : ManagementOperationOutcome.Failed;
        return journal.Complete(managementId, outcome, timeProvider.GetUtcNow())
            ? result : result with { Outcome = WorkspaceRuntimeReconciliationOutcome.EvidenceUnconfirmed, HostedServicesRestored = false };

        async Task<bool> PlanMatchesAsync()
        {
            ct.ThrowIfCancellationRequested();
            if (digest != WorkspaceRuntimeResourceSet.Digest(state))
                return false;
            var current = await DescribeServicesAsync(state, ct);
            return services is null ? current is null
                : current is { BlockReason: null } && current.Digest == request.ExpectedHostedServiceDigest;
        }
    }

    private async Task<WorkspaceRuntimeReconciliationResult> ConfirmAsync(WorkspaceState state,
        WorkspaceRuntimeSnapshot observed, WorkspaceRuntimeReadiness readiness, Func<CancellationToken, Task> persist)
    {
        var previousInstance = state.RuntimeInstanceId;
        var previousCondition = state.RecoveryCondition;
        state.RuntimeInstanceId = runtime.InstanceId;
        state.RecoveryCondition = WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost;
        // Once the state write begins, caller cancellation cannot claim that no local effect occurred.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        try
        {
            await persist(completion.Token);
        }
        catch (Exception)
        {
            // A provider can fail after committing. Keep admission unknown and restore the conservative live view.
            state.RuntimeInstanceId = previousInstance;
            state.RecoveryCondition = previousCondition;
            return new() { Outcome = WorkspaceRuntimeReconciliationOutcome.OutcomeUnknown };
        }
        return new()
        {
            Outcome = WorkspaceRuntimeReconciliationOutcome.Confirmed,
            Observation = observed with
            {
                RecoveryCondition = state.RecoveryCondition.ToString(),
                StartedOnCurrentHost = false,
                ConfirmedOnCurrentHost = true,
                Readiness = readiness,
                ResourceSetDigest = WorkspaceRuntimeResourceSet.Digest(state)
            }
        };
    }
}
