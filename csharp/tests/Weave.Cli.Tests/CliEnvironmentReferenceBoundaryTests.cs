namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliEnvironmentReferenceBoundaryTests
{
    [Fact]
    public Task ResolveReference_EnvironmentSourceRemoved_StopsResolvingInsteadOfCachingSecret() =>
        SiloLauncherProcessHarness.RunAsync(typeof(CliEnvironmentReferenceBoundaryTests), _ =>
        {
            const string variable = "WEAVE_TEST_ISOLATED_REFERENCE";
            var previous = Environment.GetEnvironmentVariable(variable);
            var resolver = new CliSecretResolver();
            try
            {
                Environment.SetEnvironmentVariable(variable, "Host=fixture-env;Password=fake-only");
                resolver.ResolveReference("EnV:  " + variable + "  ").ShouldBe("Host=fixture-env;Password=fake-only");

                Environment.SetEnvironmentVariable(variable, null);
                var missing = Should.Throw<InvalidOperationException>(() => resolver.ResolveReference("env:" + variable));
                missing.Message.ShouldContain(variable);
                missing.Message.ShouldContain("is not set");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
            return Task.CompletedTask;
        });
}
