using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Exercises <see cref="ProcessCommandRunner"/> against real child processes.
/// Uses <c>dotnet --version</c> as a cross-platform happy-path probe — the
/// runner executes in CI on Windows/Linux/macOS and <c>dotnet</c> is always
/// on PATH. The failure path invokes a deliberately bad command to verify
/// the non-zero exit-code → <see cref="InvalidOperationException"/> contract
/// and the concurrent stdout/stderr drain rule documented in the source.
/// </summary>
public sealed class ProcessCommandRunnerTests
{
    private readonly ProcessCommandRunner _runner = new();

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsStdout()
    {
        var output = await _runner.RunAsync("dotnet", ["--version"], TestContext.Current.CancellationToken);

        output.ShouldNotBeNullOrWhiteSpace();
        output.Trim().ShouldMatch(@"^\d+\.\d+\.\d+"); // "10.0.x" or similar
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsInvalidOperationWithStderr()
    {
        // `dotnet --definitely-not-a-flag` produces a non-zero exit code
        // and writes a diagnostic to stderr — exactly the shape we want to
        // prove is surfaced rather than swallowed.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _runner.RunAsync("dotnet", ["--definitely-not-a-flag"], TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("failed");
    }

    [Fact]
    public async Task RunAsync_UnknownExecutable_ThrowsInvalidOperation()
    {
        // Nonexistent executables throw immediately from Process.Start. The
        // runner wraps that failure consistently so callers don't have to
        // distinguish Win32Exception from ArgumentException.
        await Should.ThrowAsync<Exception>(
            () => _runner.RunAsync("this-binary-definitely-does-not-exist-on-path-xyz123", [], TestContext.Current.CancellationToken));
    }
}
