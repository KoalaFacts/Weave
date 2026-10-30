using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliCredentialDenialProcessTests
{
    [Fact]
    public Task GuardAsync_CredentialIssuanceDenied_ReportsUnconfirmedHostRequestWithoutAnonymousFallback() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliCredentialDenialProcessTests), async root =>
        {
            await using var server = new LocalCliLoopbackServer(
                new LocalCliHttpStep("POST", "/api/operator/credentials/agent/issue", 403, "{}"));
            using var fixture = new LocalCliBoundaryFixture(root, server.Port);

            var result = await LocalCliCommand.GuardAsync(() => fixture.Command.StatusAsync(fixture.DirectoryPath,
                "82c07b3d2a3646e88f2f0b8db07c452b", TestContext.Current.CancellationToken));
            await server.CompleteAsync();

            result.ShouldBe(1);
            fixture.Error.ToString().ShouldContain("Host request was not confirmed.");
            fixture.Error.ToString().ShouldContain("query the original UUID before retrying");
            fixture.Output.ToString().ShouldBeEmpty();
            server.Headers.ShouldHaveSingleItem()["X-Weave-Operator-Key"].ShouldBe(LocalCliBoundaryFixture.OperatorKey);
            fixture.AssertPreserved();
        });
}
