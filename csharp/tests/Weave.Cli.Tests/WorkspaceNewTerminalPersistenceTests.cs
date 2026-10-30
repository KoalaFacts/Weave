using System.Text.Json;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceNewTerminalPersistenceTests
{
    [Theory]
    [InlineData("workspace-new-no-authority", "my-workspace", "fixture-model", IsolationLevel.Full, "no-grants-created")]
    [InlineData("workspace-new-explicit-authority", "team with spaces", "gpt-4o", IsolationLevel.Shared, "file-grant-created")]
    [InlineData("workspace-new-no-tools", "no-tools", "claude-sonnet-4-20250514", IsolationLevel.None, "none-created")]
    [InlineData("workspace-new-preset", "preset-team", "", IsolationLevel.Full, "preset-created")]
    public async Task WorkspaceNew_InteractiveSelections_PersistsOnlyChosenAvailabilityAndAuthority(
        string scenario, string name, string model, IsolationLevel isolation, string completedStage)
    {
        using var fixture = new InitWizardProcessFixture();
        using var result = await fixture.RunAsync(scenario);
        var stages = result.RootElement.GetProperty("stages").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        stages.ShouldContain(completedStage);
        var explicitGrant = scenario == "workspace-new-explicit-authority";
        var destination = explicitGrant
            ? Path.Join(fixture.Root, "destination with spaces")
            : Path.Join(fixture.Workspace, name);
        var manifest = await WorkspaceManifestFile.ReadAsync(Path.Join(destination, "workspace.json"),
            TestContext.Current.CancellationToken);

        manifest.Name.ShouldBe(name);
        manifest.Workspace.Isolation.ShouldBe(isolation);
        manifest.Workspace.Network.ShouldNotBeNull().Name.ShouldBe("weave-" + name);
        manifest.Agents.Keys.ShouldBe(["assistant"]);
        var assistant = manifest.Agents["assistant"];
        assistant.Model.ShouldBe(scenario == "workspace-new-preset" ? WorkspacePresets.All["starter"].Model : model);
        string[] tools = scenario is "workspace-new-no-authority" or "workspace-new-explicit-authority"
            ? ["git", "file"] : [];
        assistant.Tools.ShouldBe(tools);
        manifest.Tools.Keys.Order().ShouldBe(tools.Order());
        foreach (var tool in manifest.Tools.Values)
            tool.Type.ShouldBe("mcp");
        string[] capabilities = explicitGrant ? ["tool:file:invoke:*"] : [];
        assistant.Capabilities.ShouldBe(capabilities);
        assistant.SystemPromptFile.ShouldBe("./prompts/assistant.md");
        (await File.ReadAllTextAsync(Path.Join(destination, "prompts", "assistant.md"),
            TestContext.Current.CancellationToken)).ShouldBe("# Assistant\n\nYou are a helpful AI assistant.\n");
        Directory.Exists(Path.Join(destination, "data")).ShouldBeTrue();
        Directory.Exists(Path.Join(destination, ".weave")).ShouldBeTrue();
        using var registry = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(fixture.Private, "workspaces.json"), TestContext.Current.CancellationToken));
        var registration = registry.RootElement.EnumerateObject().ShouldHaveSingleItem();
        registration.Name.ShouldBe(name);
        registration.Value.GetString().ShouldBe(destination);
        File.Exists(fixture.ConfigPath).ShouldBeFalse();
        var transcript = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        transcript.ShouldContain($"Workspace \"{name}\" created.");
        if (scenario == "workspace-new-no-authority")
        {
            stages.ShouldContain("default-name");
            stages.ShouldContain("custom-model-entered");
            stages.ShouldContain("git-file-available");
            transcript.ShouldContain("Leave empty to grant none");
        }
        if (scenario == "workspace-new-no-tools")
            transcript.ShouldNotContain("Allow ALL operations");
        if (scenario == "workspace-new-preset")
            transcript.ShouldNotContain("Select a model");
    }
}
