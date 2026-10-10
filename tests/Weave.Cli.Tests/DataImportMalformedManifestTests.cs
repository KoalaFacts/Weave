using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class DataImportMalformedManifestTests
{
    [Fact]
    public async Task Import_MalformedEmbeddedManifest_RetainsRecoveryFilesWithoutPostingOrClaimingServerSuccess()
    {
        using var fixture = new DataTransferFixture();
        using var output = new ShellOutputCapture();
        var export = new WorkspaceExport
        {
            WorkspaceName = "recovery-source",
            Manifest = "{",
            PromptFiles = new Dictionary<string, string> { ["recovery.md"] = "# Retained recovery content\n" }
        };
        await fixture.WriteExportAsync(export);
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(200)
            : throw new InvalidOperationException("Invalid manifest must not reach server restoration: " + path);

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe("/health");
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "workspace.json"), TestContext.Current.CancellationToken)).ShouldBe("{");
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "prompts", "recovery.md"), TestContext.Current.CancellationToken))
            .ShouldBe(export.PromptFiles["recovery.md"]);
        fixture.Registry.Registrations.ShouldHaveSingleItem().ShouldBe(
            new KeyValuePair<string, string>(fixture.Destination, Path.GetFullPath(fixture.Destination)));
        File.Exists(Path.Join(fixture.Destination, ".weave", "workspace-id")).ShouldBeFalse();
        output.Text.ShouldContain("Could not parse manifest:");
        output.Text.ShouldContain("Files are restored — start manually with: weave run");
        output.Text.ShouldContain("server restoration did not complete.");
        output.Text.ShouldNotContain("Workspace started");
        output.Text.ShouldNotContain("imported to");
    }
}
