using System.Security.Cryptography;
using System.Text;
using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.InstallMcpTool;
using Weave.Tools.Tool;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.RuntimeRecovery;

internal sealed class McpWorkspaceServiceRecovery(
    IVirtualActorProvider actors,
    IMcpInstallationDispatchGate installations,
    IToolInstallationPeerProbe probe,
    ICapabilityAuthorizer authorizer) : IWorkspaceHostedServiceRecovery
{
    public async Task<WorkspaceHostedServicePlan> DescribeAsync(WorkspaceHostedServices services, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (services.McpInstallations.Count == 0
            || services.McpInstallations.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != services.McpInstallations.Count
            || services.McpInstallations.Select(item => item.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != services.McpInstallations.Count
            || services.McpInstallations.Any(item => !ValidInstallation(services.WorkspaceId, item))
            || services.Plugins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != services.Plugins.Count
            || services.Plugins.Any(name => !services.McpInstallations.Any(item => item.PluginName == name)))
            return new();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(services.WorkspaceId));
        var tools = await registry.GetMcpRecoveryPlansAsync(ct);
        if (!services.Tools.Order(StringComparer.Ordinal).SequenceEqual(tools.Select(item => item.Name).Order(StringComparer.Ordinal))
            || tools.Any(tool => !tool.Supported || !services.McpInstallations.Any(item =>
                item.PluginName == tool.PluginName && item.Url == tool.Url && item.Operation == tool.Name)))
            return new();
        return new()
        {
            BlockReason = null,
            Digest = Digest(services, tools),
            McpInstallations = services.McpInstallations,
            RequiredGrants = services.McpInstallations.Select(item => $"plugin:invoke:{item.Id}")
                .Concat(tools.Select(item => ToolCapability.Connect(item.Name)))
                .Order(StringComparer.Ordinal).ToArray()
        };
    }

    public async Task<string?> RestoreAsync(WorkspaceHostedServices services, string expectedDigest, CapabilityToken token, CancellationToken ct)
    {
        var plan = await DescribeAsync(services, ct);
        if (plan.BlockReason is not null || plan.Digest != expectedDigest)
            return "hosted-service-plan-changed";
        await AuthorizeAsync();
        var peerFailure = await ProbeCurrentAsync();
        if (peerFailure is not null)
            return peerFailure;
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(services.WorkspaceId));
        var tools = await registry.GetMcpRecoveryPlansAsync(ct);
        foreach (var tool in tools)
        {
            await AuthorizeAsync();
            if ((await DescribeAsync(services, ct)).Digest != expectedDigest)
                return "hosted-service-plan-changed";
            try
            {
                if (!await registry.RestoreMcpToolAsync(tool.Name, tool.Digest, token, ct))
                    return "mcp-tool-restoration-unconfirmed";
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
            {
                return "mcp-peer-unavailable";
            }
            catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
            {
                return "mcp-tool-restoration-unconfirmed";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return "mcp-peer-unavailable";
            }
        }
        // Probe the pinned peer afresh after establishing tool handles. Cached installation readiness is insufficient.
        peerFailure = await ProbeCurrentAsync();
        if (peerFailure is not null)
            return peerFailure;
        return (await DescribeAsync(services, ct)).Digest == expectedDigest ? null : "hosted-service-plan-changed";

        async Task<string?> ProbeCurrentAsync()
        {
            foreach (var installation in services.McpInstallations)
            {
                await AuthorizeAsync();
                if (!installations.MatchesInstallation(installation))
                    return "mcp-installation-not-restored";
                var failure = await probe.ProbeAsync(new McpToolInstallation
                {
                    Url = installation.Url,
                    ServerName = installation.ServerName,
                    ServerVersion = installation.ServerVersion,
                    Operation = installation.Operation,
                    ContractDigest = installation.ContractDigest
                }, ct);
                if (failure is not InstallationFailureCode.None)
                    return failure switch
                    {
                        InstallationFailureCode.ContractRejected => "mcp-contract-rejected",
                        InstallationFailureCode.PeerUnavailable => "mcp-peer-unavailable",
                        _ => "mcp-connection-unconfirmed"
                    };
                if (!installations.MatchesInstallation(installation))
                    return "mcp-installation-not-restored";
            }
            return null;
        }

        async Task AuthorizeAsync()
        {
            ct.ThrowIfCancellationRequested();
            await authorizer.AuthorizeAsync(token, WorkspaceRuntimeRecovery.ReconcileGrant, services.WorkspaceId);
            foreach (var grant in plan.RequiredGrants)
                await authorizer.AuthorizeAsync(token, grant, services.WorkspaceId);
            ct.ThrowIfCancellationRequested();
        }
    }

    private static bool ValidInstallation(string workspaceId, McpToolInstallationSnapshot installation) =>
        installation.Id == $"{workspaceId}/{installation.PluginName}" && !string.IsNullOrWhiteSpace(installation.PluginName)
        && !installation.HasUnsupportedAuthority && installation.DefinitionRevision == McpToolInstallation.ImplementationRevision
        && installation.ConfigDigest == McpToolInstallation.ComputeConfigDigest(installation.Url, installation.ServerName,
            installation.ServerVersion, installation.Operation)
        && installation.ContractDigest.Length == 64 && installation.ContractDigest.All(char.IsAsciiHexDigit)
        && !string.IsNullOrWhiteSpace(installation.ServerName) && !string.IsNullOrWhiteSpace(installation.ServerVersion)
        && !string.IsNullOrWhiteSpace(installation.Operation)
        && Uri.TryCreate(installation.Url, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Host == "127.0.0.1" && endpoint.Port is >= 1 and <= 65535
        && endpoint.AbsolutePath == "/mcp" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0;

    private static string Digest(WorkspaceHostedServices services, IReadOnlyList<McpToolRecoveryPlan> tools)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("weave-mcp-workspace-services-v1");
            writer.Write(services.WorkspaceId);
            writer.Write(services.McpInstallations.Count);
            foreach (var item in services.McpInstallations.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                writer.Write(item.Id);
                writer.Write(item.PluginName);
                writer.Write(item.DefinitionRevision);
                writer.Write(item.ConfigDigest);
                writer.Write(item.ContractDigest);
            }
            writer.Write(tools.Count);
            foreach (var tool in tools.OrderBy(item => item.Name, StringComparer.Ordinal))
                writer.Write(tool.Digest);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
