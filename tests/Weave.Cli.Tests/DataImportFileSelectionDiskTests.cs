using System.Text.Json;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class DataImportFileSelectionDiskTests
{
    [Fact]
    public Task Import_SelectsSecondDiscoveredExportAndRestoresItsFilesAndStartedWorkspaceId() =>
        SiloLauncherProcessHarness.RunAsync(typeof(DataImportFileSelectionDiskTests), async root =>
        {
            using var fixture = new DataTransferFixture();
            var first = Path.Join(root, "first-export.json");
            var selected = Path.Join(root, "selected.export.json");
            const string invalidExport = "unselected-invalid-export-marker";
            await File.WriteAllTextAsync(first, invalidExport, TestContext.Current.CancellationToken);
            var export = new WorkspaceExport
            {
                WorkspaceName = "selected-source",
                Manifest = """{"version":"1.0","name":"selected-source"}""",
                PromptFiles = new Dictionary<string, string> { ["assistant.md"] = "# Selected prompt\nRetain [literal] text.\n" }
            };
            var json = JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport);
            await File.WriteAllTextAsync(selected, json, TestContext.Current.CancellationToken);
            fixture.Respond = path => path switch
            {
                "/health" => DataTransferFixture.Response(200),
                "/api/workspaces" => DataTransferFixture.Response(201,
                    """{"workspaceId":"selected-started-id","name":"selected-source","status":"Running","recoveryCondition":"StartedOnThisHost","containerCount":0}"""),
                _ => throw new InvalidOperationException("Empty collections must not send restoration posts: " + path)
            };
            using var console = new ScriptedSelectionConsole(ConsoleKey.DownArrow, ConsoleKey.Enter);

            var result = await fixture.ImportCommand().ExecuteAsync(
                new DataImportOptions(null, fixture.Destination), TestContext.Current.CancellationToken);

            result.ShouldBe(0);
            console.RemainingKeys.ShouldBe(0);
            console.Output.ShouldContain("Which export file would you like to import?");
            console.Output.ShouldContain("first-export.json");
            console.Output.ShouldContain("selected.export.json");
            console.Output.ShouldContain("Workspace started (ID: selected-started-id).");
            console.Output.ShouldNotContain("server restoration did not complete");
            (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "workspace.json"), TestContext.Current.CancellationToken)).ShouldBe(export.Manifest);
            (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "prompts", "assistant.md"), TestContext.Current.CancellationToken))
                .ShouldBe(export.PromptFiles["assistant.md"]);
            (await File.ReadAllTextAsync(Path.Join(fixture.Destination, ".weave", "workspace-id"), TestContext.Current.CancellationToken))
                .ShouldBe("selected-started-id");
            (await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken)).ShouldBe(invalidExport);
            (await File.ReadAllTextAsync(selected, TestContext.Current.CancellationToken)).ShouldBe(json);
            fixture.Requests.Select(request => request.Method + " " + request.Path).ShouldBe(["GET /health", "POST /api/workspaces"]);
            var start = fixture.Requests[1];
            start.Body.ShouldNotBeNull()["manifest"].ShouldNotBeNull()["name"].ShouldNotBeNull().GetValue<string>()
                .ShouldBe("selected-source");
            fixture.Registry.Registrations.ShouldHaveSingleItem().ShouldBe(
                new KeyValuePair<string, string>(fixture.Destination, Path.GetFullPath(fixture.Destination)));
        });
}
