namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliReviewProcessTests
{
    [Fact]
    public Task ReviewAsync_Noninteractive_RefusesDecisionAndContinuation() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliReviewProcessTests), async root =>
        {
            await using var server = new LocalCliLoopbackServer(
                new LocalCliHttpStep("POST", "/api/operator/credentials/reviewer/issue", 200, LocalCliBoundaryFixture.Capability, true));
            using var fixture = new LocalCliBoundaryFixture(root, server.Port);

            var result = await fixture.Command.ReviewAsync(fixture.DirectoryPath,
                "82C07B3D-2A36-46E8-8F2F-0B8DB07C452B", true, "must-not-execute", "agent directory",
                TestContext.Current.CancellationToken);
            await server.CompleteAsync();

            result.ShouldBe(1);
            fixture.Review.Lines.ShouldContain("Human review requires an interactive terminal. No decision was sent.");
            server.Headers.ShouldHaveSingleItem()["X-Weave-Operator-Key"].ShouldBe(LocalCliBoundaryFixture.OperatorKey);
            server.Headers[0].ContainsKey("X-Weave-Capability").ShouldBeFalse();
            fixture.Output.ToString().ShouldNotContain(LocalCliBoundaryFixture.Capability);
            fixture.AssertPreserved();
        });
}
