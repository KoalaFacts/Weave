namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliFileReferenceBoundaryTests
{
    [Fact]
    public Task ResolveReference_FileSourceRemoved_StopsResolvingInsteadOfCachingSecret() =>
        SiloLauncherProcessHarness.RunAsync(typeof(CliFileReferenceBoundaryTests), async root =>
        {
            var path = Path.Join(root, "fake-secret.txt");
            await File.WriteAllTextAsync(path, "  Host=fixture-file;Password=fake-only\n", TestContext.Current.CancellationToken);
            var resolver = new CliSecretResolver();
            resolver.ResolveReference("FiLe:  " + path + "  ").ShouldBe("Host=fixture-file;Password=fake-only");

            File.Delete(path);
            var missing = Should.Throw<InvalidOperationException>(() => resolver.ResolveReference("file:" + path));

            missing.Message.ShouldContain(path);
            missing.Message.ShouldContain("not found");
        });
}
