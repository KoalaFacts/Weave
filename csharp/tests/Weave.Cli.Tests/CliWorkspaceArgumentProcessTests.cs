using System.Text.Json;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliWorkspaceArgumentProcessTests
{
    [Fact]
    public async Task WorkspaceNew_ExplicitPresetAndPathWithSpaces_PersistsAtExactDestinationWithoutPrompting()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var destination = Path.Join(cli.Root, "workspace parent", "chosen destination");

        var result = await cli.RunAsync("workspace", "new", "scripted-team", "--preset", "starter", "--path", destination);

        result.ExitCode.ShouldBe(0, result.StandardError);
        result.VisibleOutput.ShouldContain("Workspace \"scripted-team\" created.");
        var manifest = await WorkspaceManifestFile.ReadAsync(Path.Join(destination, "workspace.json"),
            TestContext.Current.CancellationToken);
        manifest.Name.ShouldBe("scripted-team");
        manifest.Agents.Keys.ShouldBe(["assistant"]);
        manifest.Agents["assistant"].Capabilities.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Join(destination, "prompts", "assistant.md"),
            TestContext.Current.CancellationToken)).ShouldBe("# Assistant\n\nYou are a helpful AI assistant.\n");
        using var registry = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(cli.WeaveHome, "workspaces.json"),
            TestContext.Current.CancellationToken));
        registry.RootElement.EnumerateObject().ShouldHaveSingleItem().Name.ShouldBe("scripted-team");
        registry.RootElement.GetProperty("scripted-team").GetString().ShouldBe(destination);
        Directory.Exists(Path.Join(cli.Root, "scripted-team")).ShouldBeFalse();
    }

    [Fact]
    public async Task Run_InvalidPort_ReportsParserErrorAndPreservesExistingConfiguration()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var saved = await cli.RunAsync("config", "set", "defaultPort", "9523");
        saved.ExitCode.ShouldBe(0, saved.StandardError);
        var before = await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("run", "--port", "not-a-port");

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("not-a-port");
        result.StandardError.ShouldContain("--port");
        (await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(before);
        File.Exists(Path.Join(cli.WeaveHome, "workspaces.json")).ShouldBeFalse();
        File.Exists(Path.Join(cli.Root, "workspace.json")).ShouldBeFalse();
    }
}
