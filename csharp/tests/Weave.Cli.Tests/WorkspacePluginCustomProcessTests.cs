using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspacePluginCustomProcessTests
{
    [Fact]
    public Task AddPlugin_CustomConfiguration_PreservesReferenceTextAndAllowsEmptyDescriptionWithoutGrantingOperations() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspacePluginCustomProcessTests), async root =>
        {
            var fixture = new WorkspacePluginDiskFixture(root);
            var secretPath = Path.Join(root, "private fixture.secret");
            await File.WriteAllTextAsync(secretPath, "synthetic-plugin-secret-not-for-display", TestContext.Current.CancellationToken);
            const string environmentReference = "env:WEAVE_PLUGIN_TEST_ONLY_TOKEN";
            var fileReference = "file:" + secretPath;
            using (var console = new MarketplacePromptConsole())
            {
                console.Line("Fixture integration description");
                console.Line("y");
                console.Line("token");
                console.Line(environmentReference);
                console.Line("y");
                console.Line("credential_file");
                console.Line(fileReference);
                console.Line("n");

                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "owned-custom", "custom"),
                    TestContext.Current.CancellationToken)).ShouldBe(0);

                console.RemainingKeys.ShouldBe(0);
                console.Text.ShouldContain("Plugin 'owned-custom' (custom) added");
                console.Text.ShouldNotContain("synthetic-plugin-secret-not-for-display");
            }
            using (var console = new MarketplacePromptConsole())
            {
                console.Line("");
                console.Line("");
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "empty-custom", "custom"),
                    TestContext.Current.CancellationToken)).ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
            }
            var manifest = await fixture.ReadAsync();
            manifest.Plugins.Keys.Order().ShouldBe(["empty-custom", "owned-custom", "retained"]);
            var configured = manifest.Plugins["owned-custom"];
            configured.Type.ShouldBe("custom");
            configured.Description.ShouldBe("Fixture integration description");
            configured.Config.Count.ShouldBe(2);
            configured.Config["token"].ShouldBe(environmentReference);
            configured.Config["credential_file"].ShouldBe(fileReference);
            configured.Requires.ShouldBeEmpty();
            configured.EnabledWhen.ShouldBeNull();
            manifest.Plugins["empty-custom"].Description.ShouldBeNull();
            manifest.Plugins["empty-custom"].Config.ShouldBeEmpty();
            File.ReadAllText(secretPath).ShouldBe("synthetic-plugin-secret-not-for-display");
            File.ReadAllText(fixture.ManifestPath).ShouldNotContain("synthetic-plugin-secret-not-for-display");
            fixture.AssertUnrelatedStatePreserved(manifest);
        });
}
