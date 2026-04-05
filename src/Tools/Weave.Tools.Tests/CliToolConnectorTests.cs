using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Tests;

public sealed class CliToolConnectorTests
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:cli-tool"]
    };

    private static CliToolConnector CreateConnector() =>
        new(Substitute.For<ILogger<CliToolConnector>>());

    private static ToolSpec CreateSpec(
        string name = "my-cli",
        string shell = "/bin/bash",
        List<string>? allowed = null,
        List<string>? denied = null) =>
        new()
        {
            Name = name,
            Type = ToolType.Cli,
            Cli = new CliConfig
            {
                Shell = shell,
                AllowedCommands = allowed ?? [],
                DeniedCommands = denied ?? []
            }
        };

    // --- ConnectAsync ---

    [Fact]
    public async Task ConnectAsync_ValidConfig_ReturnsConnectedHandle()
    {
        var connector = CreateConnector();

        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("my-cli");
        handle.Type.ShouldBe(ToolType.Cli);
        handle.ConnectionId.ShouldStartWith("cli:my-cli:");
    }

    [Fact]
    public async Task ConnectAsync_NullCliConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.Cli };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_AfterConnect_Completes()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- DiscoverSchemaAsync ---

    [Fact]
    public async Task DiscoverSchemaAsync_ReturnsCommandParameter()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("my-cli");
        schema.Description.ShouldContain("my-cli");
        schema.Parameters.ShouldContain(p => p.Name == "command" && p.Required);
    }

    [Fact]
    public void ToolType_IsCli()
    {
        CreateConnector().ToolType.ShouldBe(ToolType.Cli);
    }

    // --- Command filtering: denied commands ---

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf /home")]
    public async Task InvokeAsync_DeniedCommand_ReturnsFailure(string command)
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["rm *"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = command, Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_DeniedExactMatch_ReturnsFailure()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["shutdown"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "shutdown", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_DeniedWildcardStar_BlocksEverything()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["*"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "echo hello", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- Command filtering: allowed commands ---

    [Fact]
    public async Task InvokeAsync_AllowedCommandNotMatching_ReturnsFailure()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(allowed: ["git *"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "ls -la", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_DeniedTakesPrecedenceOverAllowed()
    {
        var connector = CreateConnector();
        // "git *" allowed, but "git push *" denied
        var spec = CreateSpec(allowed: ["git *"], denied: ["git push *"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "git push origin main", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- Command filtering: wildcard patterns ---

    [Fact]
    public async Task InvokeAsync_WildcardPrefix_MatchesSuffix()
    {
        var connector = CreateConnector();
        // Deny anything ending with "--force"
        var spec = CreateSpec(denied: ["*--force"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "git push --force", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_WildcardMiddle_MatchesInnerContent()
    {
        var connector = CreateConnector();
        // Deny "sudo * rm"
        var spec = CreateSpec(denied: ["sudo*rm"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "sudo -u root rm", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- InvokeAsync: disconnected tool ---

    [Fact]
    public async Task InvokeAsync_DisconnectedTool_ReturnsFailure()
    {
        var connector = CreateConnector();
        var spec = CreateSpec();
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "echo hi", Parameters = [] };
        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not connected");
    }

    // --- InvokeAsync: RawInput vs Parameters ---

    [Fact]
    public async Task InvokeAsync_NoRawInput_JoinsParameters()
    {
        var connector = CreateConnector();
        // Use denied list to capture what command would be formed
        var spec = CreateSpec(denied: ["echo hello"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-cli",
            Parameters = new Dictionary<string, string> { ["arg1"] = "echo", ["arg2"] = "hello" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // The joined parameters "echo hello" should match the deny pattern
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- InvokeAsync: empty allowed list permits all (when no deny) ---

    [Fact]
    public async Task InvokeAsync_EmptyAllowedAndDenied_PermitsCommand()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(shell: "nonexistent-shell-binary");
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "anything", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // Command passes filtering but process start fails (no such binary)
        result.Success.ShouldBeFalse();
        result.Error!.ShouldNotContain("not permitted");
    }
}
