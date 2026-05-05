using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceManifestFromTemplateTests
{
    [Fact]
    public void Create_ProducesManifestWithSingleAgentNamedAssistantByDefault()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.CodingAssistant, "my-app", IsolationLevel.Full);

        manifest.Name.ShouldBe("my-app");
        manifest.Version.ShouldBe("1.0");
        manifest.Agents.Count.ShouldBe(1);
        manifest.Agents.ShouldContainKey("assistant");
    }

    [Fact]
    public void Create_AgentInheritsModelToolsAndCapabilitiesFromTemplate()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.CodingAssistant, "my-app", IsolationLevel.Full);

        var agent = manifest.Agents["assistant"];
        agent.Model.ShouldBe(BuiltInTemplates.CodingAssistant.AgentDefinition.Model);
        agent.Tools.ShouldBe(["git", "files"]);
        agent.Capabilities.ShouldBe(["tool:git", "tool:files"]);
    }

    [Fact]
    public void Create_AgentSystemPromptFile_FollowsConventionalPath()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.CodingAssistant, "my-app", IsolationLevel.Full);

        manifest.Agents["assistant"].SystemPromptFile.ShouldBe("./prompts/assistant.md");
    }

    [Fact]
    public void Create_AgentSystemPromptFile_FollowsAgentNameOverride()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.SupportBot, "support-app", IsolationLevel.Full, agentName: "support-bot");

        manifest.Agents.ShouldContainKey("support-bot");
        manifest.Agents["support-bot"].SystemPromptFile.ShouldBe("./prompts/support-bot.md");
    }

    [Fact]
    public void Create_RequiredToolsBecomeManifestTools()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.CodingAssistant, "my-app", IsolationLevel.Full);

        manifest.Tools.ShouldContainKey("git");
        manifest.Tools.ShouldContainKey("files");
        manifest.Tools["git"].Type.ShouldBe("cli");
        manifest.Tools["files"].Type.ShouldBe("filesystem");
    }

    [Fact]
    public void Create_NetworkNameIsNamespacedByWorkspaceName()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "my-app", IsolationLevel.Full);

        manifest.Workspace.Network.ShouldNotBeNull();
        manifest.Workspace.Network!.Name.ShouldBe("weave-my-app");
    }

    [Fact]
    public void Create_PassesIsolationThrough()
    {
        var full = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "a", IsolationLevel.Full);
        var shared = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "b", IsolationLevel.Shared);
        var none = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "c", IsolationLevel.None);

        full.Workspace.Isolation.ShouldBe(IsolationLevel.Full);
        shared.Workspace.Isolation.ShouldBe(IsolationLevel.Shared);
        none.Workspace.Isolation.ShouldBe(IsolationLevel.None);
    }

    [Fact]
    public void Create_RegistersDefaultLocalPodmanTarget()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "my-app", IsolationLevel.Full);

        manifest.Targets.ShouldContainKey("local");
        manifest.Targets["local"].Runtime.ShouldBe("podman");
    }

    [Fact]
    public void Create_DefaultsSecretsProviderToEnv()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "my-app", IsolationLevel.Full);

        manifest.Workspace.Secrets.ShouldNotBeNull();
        manifest.Workspace.Secrets!.Provider.ShouldBe("env");
    }

    [Fact]
    public void Create_DoesNotMutateTemplateAgentDefinition()
    {
        // The primitive uses `with` to override SystemPromptFile; the original
        // template object must be untouched so callers can reuse it.
        var originalSystemPrompt = BuiltInTemplates.SupportBot.AgentDefinition.SystemPromptFile;

        WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.SupportBot, "x", IsolationLevel.Full, agentName: "support-bot");

        BuiltInTemplates.SupportBot.AgentDefinition.SystemPromptFile.ShouldBe(originalSystemPrompt);
    }

    [Fact]
    public void Create_ToolsDictionaryIsACopy_NotASharedReference()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.CodingAssistant, "my-app", IsolationLevel.Full);

        manifest.Tools.ShouldNotBeSameAs(BuiltInTemplates.CodingAssistant.RequiredTools);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsForBlankWorkspaceName(string name)
    {
        Should.Throw<ArgumentException>(() =>
            WorkspaceManifestFromTemplate.Create(BuiltInTemplates.Starter, name, IsolationLevel.Full));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsForBlankAgentName(string agentName)
    {
        Should.Throw<ArgumentException>(() =>
            WorkspaceManifestFromTemplate.Create(BuiltInTemplates.Starter, "x", IsolationLevel.Full, agentName));
    }

    [Fact]
    public void Create_ThrowsForNullTemplate()
    {
        Should.Throw<ArgumentNullException>(() =>
            WorkspaceManifestFromTemplate.Create(null!, "x", IsolationLevel.Full));
    }

    [Fact]
    public void Create_StarterTemplate_ProducesNoToolsAndEmptyCapabilities()
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            BuiltInTemplates.Starter, "minimal", IsolationLevel.Full);

        manifest.Tools.ShouldBeEmpty();
        manifest.Agents["assistant"].Tools.ShouldBeEmpty();
        manifest.Agents["assistant"].Capabilities.ShouldBeEmpty();
    }
}
