using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations.Processes;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tests.Processes;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class CliToolConnectorProcessTests
{
    [Theory]
    [InlineData("stdout-limit")]
    [InlineData("stderr-limit")]
    public async Task InvokeAsync_ExcessOutput_RejectsWithoutReturningCapturedContent(string mode)
    {
        using var child = new ProcessTestChild(mode);
        var (connector, handle) = await ConnectAsync();
        var result = await connector.InvokeAsync(handle, child.Invocation, TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("process-output-limit");
        result.Output.ShouldBeEmpty();
        result.Error.ShouldBe("Command output limit exceeded.");
    }

    [Fact]
    public async Task InvokeAsync_CancelledAfterChildStarts_TerminatesActualChild()
    {
        using var child = new ProcessTestChild("wait");
        var (connector, handle) = await ConnectAsync();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.CancelAfter(TimeSpan.FromSeconds(30));
        var running = connector.InvokeAsync(handle, child.Invocation, abort.Token);
        try
        {
            var actual = await child.GetProcessAsync(running);
            await abort.CancelAsync();
            var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => running);
            cancelled.CancellationToken.ShouldBe(abort.Token);
            await actual.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            actual.HasExited.ShouldBeTrue();
        }
        finally
        {
            await abort.CancelAsync();
            try
            { await running; }
            catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task InvokeAsync_RunnerException_DoesNotExposeExceptionBody()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ProcessResult>(new IOException("private-command-marker")));
        var (connector, handle) = await ConnectAsync(runner);
        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "process-test", RawInput = "echo hello", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("process-execution-unconfirmed");
        result.Error.ShouldBe("CLI process could not be completed.");
        result.Output.ShouldBeEmpty();
    }

    private static async Task<(CliToolConnector Connector, ToolHandle Handle)> ConnectAsync(IProcessRunner? runner = null)
    {
        var connector = new CliToolConnector(NullLogger<CliToolConnector>.Instance,
            runner ?? new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance));
        var handle = await connector.ConnectAsync(new ToolSpec
        {
            Name = "process-test",
            Type = ToolType.Cli,
            Cli = new CliConfig { Shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh" }
        }, new CapabilityToken { TokenId = "test", WorkspaceId = "process-tests", Grants = [] },
            TestContext.Current.CancellationToken);
        return (connector, handle);
    }
}
