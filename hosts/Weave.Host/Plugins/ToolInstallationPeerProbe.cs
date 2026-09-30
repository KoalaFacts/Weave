using System.Net;
using System.Text.Json;
using Weave.Tools.Connectors;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;

namespace Weave.Silo.Plugins;

internal interface IToolInstallationPeerProbe
{
    Task<InstallationFailureCode> ProbeAsync(DaprToolInstallation installation, CancellationToken cancellationToken);
    Task<InstallationFailureCode> ProbeAsync(McpToolInstallation installation, CancellationToken cancellationToken);
}

internal sealed class ToolInstallationPeerProbe(
    HttpClient httpClient,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : IToolInstallationPeerProbe, IDisposable
{
    public void Dispose() => httpClient.Dispose();

    public async Task<InstallationFailureCode> ProbeAsync(DaprToolInstallation installation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                new Uri($"http://127.0.0.1:{installation.Port}/v1.0/healthz/outbound"),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.NoContent
                ? InstallationFailureCode.None : InstallationFailureCode.PeerUnavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InstallationFailureCode.PeerUnavailable;
        }
        catch (HttpRequestException)
        {
            return InstallationFailureCode.PeerUnavailable;
        }
    }

    public async Task<InstallationFailureCode> ProbeAsync(McpToolInstallation installation,
        CancellationToken cancellationToken)
    {
        var contract = new McpInstallationContract(installation.Url, installation.ServerName,
            installation.ServerVersion, installation.Operation, installation.ContractDigest);
        var connector = new McpToolConnector(contract, loggerFactory.CreateLogger<McpToolConnector>(), timeProvider);
        try
        {
            await connector.ProbeAsync(contract.ProbeConfig(), cancellationToken);
            return InstallationFailureCode.None;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InstallationFailureCode.PeerUnavailable;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return InstallationFailureCode.PeerUnavailable;
        }
        catch (InvalidOperationException)
        {
            return InstallationFailureCode.ContractRejected;
        }
        catch (Exception error) when (error is JsonException or UnauthorizedAccessException)
        {
            return InstallationFailureCode.ConnectionFailed;
        }
        finally
        {
            await connector.DeactivateAsync();
        }
    }
}
