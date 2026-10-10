namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliCodexDispatchProcessTests
{
    [Fact]
    public Task CodexAsync_ExplicitTask_ForwardsExactLaunchArgumentsAndRejectsMissingTask() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliCodexDispatchProcessTests), async root =>
        {
            using var fixture = new LocalCliBoundaryFixture(root);
            var ct = TestContext.Current.CancellationToken;
            var alias = Path.Join(fixture.DirectoryPath, ".");
            var executable = Path.Join(root, "trusted executable with spaces");
            const string task = "Query the retained UUID; do not create a new write.";

            (await fixture.Command.CodexAsync(alias, executable, "agent directory", "  ", true, ct)).ShouldBe(1);
            fixture.Error.ToString().ShouldContain("Provide --task for a noninteractive Codex run.");
            fixture.AssertPreserved();
            (await fixture.Command.CodexAsync(alias, executable, "agent directory", task, true, ct)).ShouldBe(27);
            fixture.Launcher.Calls.ShouldHaveSingleItem().ShouldBe(
                (Path.GetFullPath(alias), fixture.Deployment, executable, "agent directory", (string?)task, true, ct));
            fixture.AssertFilesPreserved();
        });
}
