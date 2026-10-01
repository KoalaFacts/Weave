using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalCliCommand(ILocalDeploymentStore store, LocalHostRunner host, LocalCodexLauncher codex, LocalReview review, TimeProvider clock)
{
    private static readonly string[] ForeignSecrets = ["WEAVE_OPERATOR_KEY", "WEAVE_REVIEW_CAPABILITY", "Weave__Operator__Key", "CapabilityTokens__SigningKey"];
    public async Task<int> InitializeAsync(string directory, string documents, string? hostPath, string workspace, int port, CancellationToken ct)
    {
        directory = LocalDeploymentStore.Resolve(directory);
        LocalHostRunner.RequireAvailablePorts(port);
        var setup = store.Prepare(directory, documents, hostPath, workspace, port);
        if (setup.Deployment is null)
            return ReportSetupError(setup);
        var result = await host.RunAsync(directory, setup.Deployment, initialize: true, ct);
        if (result == 0)
            Console.WriteLine("Next: weave local serve, then weave local codex in another terminal. Keep using this directory for retained UUIDs.");
        return result;
    }

    public Task<int> ServeAsync(string directory, CancellationToken ct)
    {
        var setup = store.Load(directory);
        return setup.Deployment is null ? Task.FromResult(ReportSetupError(setup))
            : host.RunAsync(LocalDeploymentStore.Resolve(directory), setup.Deployment, initialize: false, ct);
    }

    public Task<int> CodexAsync(string directory, string executable, string agentDirectory, string? task, bool execute, CancellationToken ct)
    {
        var setup = store.Load(directory);
        if (execute && string.IsNullOrWhiteSpace(task))
            return Task.FromResult(ReportSetupError(new(null, "Provide --task for a noninteractive Codex run.")));
        return setup.Deployment is null ? Task.FromResult(ReportSetupError(setup))
            : codex.RunAsync(LocalDeploymentStore.Resolve(directory), setup.Deployment, executable, agentDirectory, task, execute, ct);
    }

    public async Task<int> ReviewAsync(string directory, string id, CancellationToken ct)
    {
        id = LocalInvocationClient.NormalizeId(id);
        var setup = store.Load(directory);
        if (setup.Deployment is not { } deployment)
            return ReportSetupError(setup);
        using var client = LocalHttp.CreateClient(deployment.Origin);
        var http = new LocalHttp(client, clock);
        var key = store.OperatorKey(directory);
        var capability = await http.IssueAsync("reviewer", key, ct);
        return await review.RunAsync(http, deployment.Workspace, id, capability, key, ct);
    }

    public async Task<int> StatusAsync(string directory, string id, CancellationToken ct)
    {
        id = LocalInvocationClient.NormalizeId(id);
        var setup = store.Load(directory);
        if (setup.Deployment is not { } deployment)
            return ReportSetupError(setup);
        using var client = LocalHttp.CreateClient(deployment.Origin);
        var http = new LocalHttp(client, clock);
        var capability = await http.IssueAsync("agent", store.OperatorKey(directory), ct);
        var status = await new LocalInvocationClient(http, deployment.Workspace, capability, Path.Combine(directory, "receipts")).StatusAsync(id, ct);
        Console.WriteLine(status.ToJsonString());
        return status["invocation"]!["http_status"]!.GetValue<int>() == 200
            || status["approval"]?["http_status"]?.GetValue<int>() == 200 ? 0 : 1;
    }

    public async Task<int> McpAsync(string origin, string workspace, string receipts, CancellationToken ct)
    {
        if (ForeignSecrets.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            throw new ArgumentException("Remove operator, reviewer and signing credentials from the Agent environment.");
        var capability = Environment.GetEnvironmentVariable("WEAVE_AGENT_CAPABILITY");
        if (string.IsNullOrWhiteSpace(capability) || workspace.Length is 0 or > 64
            || !workspace.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            throw new ArgumentException("An Agent capability and bounded workspace are required.");
        using var client = LocalHttp.CreateClient(origin);
        var invocations = new LocalInvocationClient(new LocalHttp(client, clock), workspace, capability, Path.GetFullPath(receipts));
        return await new LocalMcpServer(invocations).RunAsync(Console.In, Console.Out, ct);
    }

    public static async Task<int> GuardAsync(Func<Task<int>> operation)
    {
        try { return await operation(); }
        catch (ArgumentException failure)
        {
            await Console.Error.WriteLineAsync(LocalReview.Display(failure.Message));
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped. Query the original UUID before continuing any interrupted request.");
        }
        catch (HttpRequestException)
        {
            await Console.Error.WriteLineAsync("Host request was not confirmed. Check that this local Host is running; query the original UUID before retrying.");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException or FormatException)
        {
            await Console.Error.WriteLineAsync("Local runtime or configuration is unavailable. Existing data was preserved. Check the published Host, configuration permissions and installed Codex executable.");
        }
        return 1;
    }

    private static int ReportSetupError(LocalSetupResult result)
    {
        Console.Error.WriteLine(LocalReview.Display(result.Error!));
        return 1;
    }
}
