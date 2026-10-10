using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

public sealed class WorkspaceCreationPersistenceTests
{
    [Theory]
    [InlineData("multi-agent", "supervisor", "worker")]
    [InlineData("support-team", "support-bot", "monitor")]
    public async Task ExecuteAsync_TeamPreset_PersistsAgentsPromptsAndExplicitGrants(
        string presetName, string primary, string secondary)
    {
        using var directory = new LocalTestDirectory();
        var destination = Path.Join(directory.Root, "created");
        var registry = new RecordingWorkspaceRegistry();
        var command = new WorkspaceNewCliCommand(registry);

        var result = await command.ExecuteAsync(new WorkspaceNewOptions("team", presetName, destination),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var manifest = await WorkspaceManifestFile.ReadAsync(Path.Join(destination, "workspace.json"),
            TestContext.Current.CancellationToken);
        manifest.Name.ShouldBe("team");
        manifest.Workspace.Isolation.ShouldBe(IsolationLevel.Full);
        manifest.Workspace.Network.ShouldNotBeNull().Name.ShouldBe("weave-team");
        manifest.Workspace.Secrets.ShouldNotBeNull().Provider.ShouldBe("env");
        manifest.Agents.Keys.Order().ShouldBe(new[] { primary, secondary }.Order());
        var preset = WorkspacePresets.All[presetName];
        manifest.Agents[primary].Capabilities.ShouldBe(preset.Capabilities);
        manifest.Agents[primary].Tools.ShouldBe(preset.Tools);
        manifest.Tools.Keys.Order().ShouldBe(preset.ToolDefinitions.Keys.Order());
        foreach (var (name, agent) in manifest.Agents)
        {
            agent.SystemPromptFile.ShouldBe($"./prompts/{name}.md");
            var prompt = await File.ReadAllTextAsync(Path.Join(destination, "prompts", name + ".md"),
                TestContext.Current.CancellationToken);
            prompt.ShouldContain(name == "support-bot" ? "# Support Bot" : "# " + char.ToUpperInvariant(name[0]) + name[1..]);
        }
        if (presetName == "support-team")
        {
            manifest.Agents[secondary].Tools.ShouldBe(["web-search"]);
            manifest.Agents[secondary].Capabilities.ShouldBe(["tool:web-search:invoke:*"]);
            manifest.Channels.Keys.Order().ShouldBe(preset.Channels.ShouldNotBeNull().Keys.Order());
            manifest.Channels.ShouldNotBeEmpty();
        }
        else
        {
            manifest.Agents[secondary].Capabilities.ShouldBe(preset.Capabilities);
            manifest.Agents[secondary].Model.ShouldBe(preset.Model);
            manifest.Channels.ShouldBeEmpty();
        }
        manifest.Targets["local"].Runtime.ShouldBe("podman");
        Directory.Exists(Path.Join(destination, "data")).ShouldBeTrue();
        Directory.Exists(Path.Join(destination, ".weave")).ShouldBeTrue();
        registry.Registrations.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("team", Path.GetFullPath(destination)));
    }

    [Fact]
    public async Task ExecuteAsync_StarterPreset_PersistsWorkspaceWithoutToolAuthority()
    {
        using var directory = new LocalTestDirectory();
        var destination = Path.Join(directory.Root, "starter");
        var command = new WorkspaceNewCliCommand(new RecordingWorkspaceRegistry());

        (await command.ExecuteAsync(new WorkspaceNewOptions("starter", "starter", destination),
            TestContext.Current.CancellationToken)).ShouldBe(0);

        var manifest = await WorkspaceManifestFile.ReadAsync(Path.Join(destination, "workspace.json"),
            TestContext.Current.CancellationToken);
        manifest.Agents.Keys.ShouldBe(["assistant"]);
        manifest.Agents["assistant"].Capabilities.ShouldBeEmpty();
        manifest.Tools.ShouldBeEmpty();
        manifest.Channels.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Join(destination, "prompts", "assistant.md"),
            TestContext.Current.CancellationToken)).ShouldBe("# Assistant\n\nYou are a helpful AI assistant.\n");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownPreset_DoesNotCreateOrRegisterWorkspace()
    {
        using var directory = new LocalTestDirectory();
        var destination = Path.Join(directory.Root, "rejected");
        var registry = new RecordingWorkspaceRegistry();

        var result = await new WorkspaceNewCliCommand(registry).ExecuteAsync(
            new WorkspaceNewOptions("rejected", "unknown-preset", destination), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        Directory.Exists(destination).ShouldBeFalse();
        registry.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public void Create_CustomSelection_PreservesRestrictedAuthorityIndependentlyOfTools()
    {
        var selection = new WorkspaceNewSelection("custom-model", ["files", "web"], null,
            IsolationLevel.Full, ["tool:files:invoke:read"]);

        var template = WorkspaceNewTemplateFactory.Create(selection);
        var tools = WorkspaceNewTemplateFactory.CreateTools(selection);

        template.Agents.Keys.ShouldBe(["assistant"]);
        template.Agents["assistant"].Model.ShouldBe("custom-model");
        template.Agents["assistant"].Tools.ShouldBe(["files", "web"]);
        template.Agents["assistant"].Capabilities.ShouldBe(["tool:files:invoke:read"]);
        tools.Keys.Order().ShouldBe(["files", "web"]);
        tools.Values.ShouldAllBe(tool => tool.Type == "mcp");
        WorkspaceNewTemplateFactory.CreateChannels(selection).ShouldBeEmpty();
    }
}
