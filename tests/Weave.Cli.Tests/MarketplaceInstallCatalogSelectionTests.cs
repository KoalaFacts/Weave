using System.Net;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallCatalogSelectionTests
{
    [Fact]
    public Task Install_SelectsCorrectIdAmongSameNameItems_AndScaffoldsResolvedTemplate() =>
        SiloLauncherProcessHarness.RunAsync(typeof(MarketplaceInstallCatalogSelectionTests), async root =>
        {
            using var console = new MarketplacePromptConsole();
            console.Key(ConsoleKey.DownArrow);
            console.Key(ConsoleKey.Enter, '\r');
            using var fixture = new MarketplaceInstallFlowFixture(new WorkspaceRegistry());
            fixture.Respond = (request, _) => Task.FromResult(request.RequestUri.ShouldNotBeNull().PathAndQuery switch
            {
                "/health" => MarketplaceInstallFlowFixture.Response(HttpStatusCode.OK),
                "/api/marketplace" => MarketplaceInstallFlowFixture.Response(HttpStatusCode.OK, """
                    [
                      {"itemId":"unselected-item","name":"Review bundle","description":"First candidate","category":"tools","version":"1.0","author":"fixture","status":"Published","installCount":0},
                      {"itemId":"item?owned","name":"Review bundle","description":"Selected candidate","category":"tools","version":"2.3.4","author":"fixture","status":"Published","installCount":16}
                    ]
                    """),
                MarketplaceInstallFlowFixture.InstallPath => MarketplaceInstallFlowFixture.Response(HttpStatusCode.OK,
                    MarketplaceInstallFlowFixture.InstallJson),
                var path => throw new InvalidOperationException("Unexpected install-selection request: " + path)
            });
            const string workspaceName = "selected-catalog-workspace";
            var destination = Path.Join(root, workspaceName);

            var result = await fixture.Command.ExecuteAsync(
                new MarketplaceInstallOptions(null, WorkspaceName: workspaceName), TestContext.Current.CancellationToken);

            result.ShouldBe(0);
            console.RemainingKeys.ShouldBe(0);
            fixture.Requests.ShouldBe(["GET /health", "GET /api/marketplace", "POST " + MarketplaceInstallFlowFixture.InstallPath]);
            console.Text.ShouldContain("Which marketplace item would you like to install?");
            console.Text.ShouldContain("Review bundle (unselected-item)");
            console.Text.ShouldContain("Review bundle (item?owned)");
            console.Text.ShouldContain("Installed: Review bundle [literal]");
            console.Text.ShouldContain("Workspace \"selected-catalog-workspace\" scaffolded");
            var parser = new ManifestParser();
            var manifest = parser.ParseFile(Path.Join(destination, "workspace.json"));
            parser.Validate(manifest).ShouldBeEmpty();
            manifest.Name.ShouldBe(workspaceName);
            var agent = manifest.Agents.ShouldHaveSingleItem().Value;
            agent.Model.ShouldBe("fixture-model");
            agent.Tools.ShouldBe(["documents"]);
            agent.Capabilities.ShouldBe(["tool:documents:invoke:read"]);
            agent.SystemPromptFile.ShouldBe("./prompts/assistant.md");
            manifest.Tools.ShouldHaveSingleItem().Key.ShouldBe("documents");
            (await File.ReadAllTextAsync(Path.Join(destination, "prompts", "assistant.md"), TestContext.Current.CancellationToken))
                .ShouldBe("# Review template [literal]\n\nReview local documents.\n");
            new WorkspaceRegistry().Resolve(workspaceName).ShouldBe(destination);
        });
}
