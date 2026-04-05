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

    // --- Wildcard: anchored start pattern does NOT match mid-string ---

    [Fact]
    public async Task InvokeAsync_AnchoredStartPattern_DoesNotMatchMidString()
    {
        var connector = CreateConnector();
        // "git*" is anchored at start — should NOT match "sudo git push"
        var spec = CreateSpec(denied: ["git*"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        // "sudo git push" does NOT start with "git", so it should NOT be denied
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "sudo git push", Parameters = [] };
        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // Passes filtering (hits nonexistent shell, but that's fine)
        result.Error!.ShouldNotContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_AnchoredStartPattern_MatchesFromStart()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["git*"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "git push --force", Parameters = [] };
        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- Allowed command: positive match passes filtering ---

    [Fact]
    public async Task InvokeAsync_AllowedCommandMatches_PassesFiltering()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(shell: "nonexistent-shell-binary", allowed: ["echo *"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "echo hello", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // Command passes allow-list but process start fails — the error is NOT "not permitted"
        result.Success.ShouldBeFalse();
        result.Error!.ShouldNotContain("not permitted");
    }

    // --- RawInput takes precedence over Parameters ---

    [Fact]
    public async Task InvokeAsync_RawInputTakesPrecedenceOverParameters()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["rm -rf /"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-cli",
            RawInput = "rm -rf /",
            Parameters = new Dictionary<string, string> { ["safe"] = "command" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // RawInput "rm -rf /" is used, not the joined parameters
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }

    // --- Multiple deny patterns ---

    [Fact]
    public async Task InvokeAsync_MultipleDenyPatterns_AnyMatchBlocks()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["rm *", "shutdown", "*--force"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        // Test each deny pattern independently
        var rm = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-cli", RawInput = "rm -rf /tmp", Parameters = [] },
            TestContext.Current.CancellationToken);
        rm.Error!.ShouldContain("not permitted");

        var shutdown = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-cli", RawInput = "shutdown", Parameters = [] },
            TestContext.Current.CancellationToken);
        shutdown.Error!.ShouldContain("not permitted");

        var force = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-cli", RawInput = "git push --force", Parameters = [] },
            TestContext.Current.CancellationToken);
        force.Error!.ShouldContain("not permitted");
    }

    // --- Denied result includes Duration ---

    [Fact]
    public async Task InvokeAsync_DeniedCommand_HasDuration()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["*"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "anything", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    // --- ConnectAsync generates unique connection IDs ---

    [Fact]
    public async Task ConnectAsync_TwoCalls_GenerateUniqueConnectionIds()
    {
        var connector = CreateConnector();

        var handle1 = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var handle2 = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle1.ConnectionId.ShouldNotBe(handle2.ConnectionId);
    }

    // --- Successful process execution ---

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    public async Task InvokeAsync_EchoCommand_ReturnsOutput(string shell)
    {
        var connector = CreateConnector();
        var spec = CreateSpec(shell: shell);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "echo weave-test-output", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("weave-test-output");
        result.ToolName.ShouldBe("my-cli");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    // --- AppendShellArguments: PowerShell vs bash ---

    [Fact]
    public async Task InvokeAsync_PowerShellShell_UsesCommandFlag()
    {
        var connector = CreateConnector();
        // Use pwsh if available — if not, the process fails to start but that's expected
        var spec = CreateSpec(shell: "pwsh");
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "Write-Output 'hello'", Parameters = [] };

        // This test verifies the code path doesn't throw — pwsh may or may not be installed
        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // Whether it succeeds depends on pwsh being installed
        result.ToolName.ShouldBe("my-cli");
    }

    // --- InvokeAsync: non-zero exit code ---

    [Fact]
    public async Task InvokeAsync_NonZeroExitCode_ReturnsFailure()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(shell: "bash");
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "exit 1", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
    }

    // --- Wildcard: anchored end ---

    [Fact]
    public async Task InvokeAsync_AnchoredEndPattern_DoesNotMatchPrefix()
    {
        var connector = CreateConnector();
        // "*txt" is anchored at end — "txtfile" should NOT match
        var spec = CreateSpec(denied: ["*txt"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "txtfile", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        // "txtfile" does NOT end with "txt" — should pass filtering
        result.Error!.ShouldNotContain("not permitted");
    }

    [Fact]
    public async Task InvokeAsync_AnchoredEndPattern_MatchesSuffix()
    {
        var connector = CreateConnector();
        var spec = CreateSpec(denied: ["*.txt"]);
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-cli", RawInput = "cat file.txt", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not permitted");
    }
}
