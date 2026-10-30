using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspacePluginTemplateProcessTests
{
    [Fact]
    public Task AddPlugin_TemplateMenuAndExplicitType_PersistSelectedValuesWithoutChangingTemplateDefaultsOrAuthority() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspacePluginTemplateProcessTests), async root =>
        {
            var fixture = new WorkspacePluginDiskFixture(root);
            using (var console = new MarketplacePromptConsole())
            {
                console.Line("");
                console.Line("");
                console.Line("");
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, null, null),
                    TestContext.Current.CancellationToken)).ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
                console.Text.ShouldContain("Select plugin type:");
                console.Text.ShouldContain("Plugin name:");
                console.Text.ShouldContain("Plugin 'dapr' (dapr) added");
            }
            using (var console = new MarketplacePromptConsole())
            {
                console.Line("http://127.0.0.1:1/fixture-only");
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "custom-endpoint", "http"),
                    TestContext.Current.CancellationToken)).ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
                console.Text.ShouldNotContain("Select plugin type:");
                console.Text.ShouldNotContain("Plugin name:");
            }
            using (var console = new MarketplacePromptConsole())
            {
                console.Line("");
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "default-endpoint", "http"),
                    TestContext.Current.CancellationToken)).ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
            }
            var manifest = await fixture.ReadAsync();
            manifest.Plugins.Keys.Order().ShouldBe(["custom-endpoint", "dapr", "default-endpoint", "retained"]);
            manifest.Plugins["dapr"].Type.ShouldBe("dapr");
            manifest.Plugins["dapr"].Description.ShouldBe("Dapr sidecar for pub/sub events and service invocation");
            manifest.Plugins["dapr"].Config.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("port", "3500"));
            manifest.Plugins["custom-endpoint"].Type.ShouldBe("http");
            manifest.Plugins["custom-endpoint"].Description.ShouldBe("Generic HTTP/REST endpoint");
            manifest.Plugins["custom-endpoint"].Config["base_url"].ShouldBe("http://127.0.0.1:1/fixture-only");
            manifest.Plugins["default-endpoint"].Config["base_url"].ShouldBe("http://localhost:8080");
            manifest.Plugins["custom-endpoint"].Requires.ShouldBeEmpty();
            manifest.Plugins["custom-endpoint"].EnabledWhen.ShouldBeNull();
            fixture.AssertUnrelatedStatePreserved(manifest);
        });
}
