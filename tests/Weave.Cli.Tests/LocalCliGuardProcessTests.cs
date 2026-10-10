using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliGuardProcessTests
{
    [Fact]
    public Task GuardAsync_InvalidIdMissingAndCorruptDeployment_ReportsFailureAndPreservesEvidence() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliGuardProcessTests), async root =>
        {
            using var fixture = new LocalCliBoundaryFixture(root);
            var ct = TestContext.Current.CancellationToken;
            (await LocalCliCommand.GuardAsync(() => fixture.Command.StatusAsync(fixture.DirectoryPath, "invalid-id", ct))).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("Use the original nonzero invocation UUID.");
            fixture.AssertPreserved();

            var missing = Path.Join(root, "missing deployment");
            fixture.Error.GetStringBuilder().Clear();
            (await fixture.Command.ServeAsync(missing, ct)).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("No ready local deployment was found.");
            fixture.Error.GetStringBuilder().Clear();
            (await fixture.Command.CodexAsync(missing, "never-run", "agent", "task", true, ct)).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("No ready local deployment was found.");
            fixture.Error.GetStringBuilder().Clear();
            (await fixture.Command.StatusAsync(missing, "82c07b3d2a3646e88f2f0b8db07c452b", ct)).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("No ready local deployment was found.");
            fixture.Error.GetStringBuilder().Clear();
            (await fixture.Command.ReviewAsync(missing, "82c07b3d2a3646e88f2f0b8db07c452b", true, "never-run", "agent", ct)).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("No ready local deployment was found.");
            Directory.Exists(missing).ShouldBeFalse();
            fixture.AssertPreserved();

            fixture.Error.GetStringBuilder().Clear();
            const string corrupt = "{invalid-json-retained-evidence";
            await File.WriteAllTextAsync(fixture.MarkerPath, corrupt, ct);
            (await LocalCliCommand.GuardAsync(() => fixture.Command.ServeAsync(fixture.DirectoryPath, ct))).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("Local runtime or configuration is unavailable. Existing data was preserved.");
            File.ReadAllText(fixture.MarkerPath).ShouldBe(corrupt);
            File.ReadAllText(fixture.ReceiptPath).ShouldBe("retained-original-fingerprint");
            fixture.Launcher.Calls.ShouldBeEmpty();
        });
}
