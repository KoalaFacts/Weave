using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

public sealed class WorkspaceEditPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AddAgent_PersistsAgentAndPreservesExistingPrompt(bool promptExists)
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WriteManifestAsync(path);
        var promptPath = Path.Join(directory.Root, "prompts", "new-agent.md");
        if (promptExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(promptPath).ShouldNotBeNull());
            await File.WriteAllTextAsync(promptPath, "# Customized prompt\nKeep me.", TestContext.Current.CancellationToken);
        }
        var resolver = new FixedManifestResolver(path);
        var command = new WorkspaceAddAgentCliCommand(resolver, Prompt(resolver));

        var result = await command.ExecuteAsync(new WorkspaceAddAgentOptions("edit-test", "new-agent", "new-model"),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var manifest = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Agents.Keys.Order().ShouldBe(["existing", "new-agent"]);
        var agent = manifest.Agents["new-agent"];
        agent.Model.ShouldBe("new-model");
        agent.SystemPromptFile.ShouldBe("./prompts/new-agent.md");
        agent.MaxConcurrentTasks.ShouldBe(3);
        agent.Tools.ShouldBeEmpty();
        agent.Capabilities.ShouldBeEmpty();
        manifest.Agents["existing"].Capabilities.ShouldBe(["tool:existing-tool:invoke:read"]);
        manifest.Tools["existing-tool"].Type.ShouldBe("filesystem");
        manifest.Targets["existing-target"].Runtime.ShouldBe("podman");
        (await File.ReadAllTextAsync(promptPath, TestContext.Current.CancellationToken)).ShouldBe(promptExists
            ? "# Customized prompt\nKeep me." : "# new-agent\n\nYou are a helpful AI assistant.\n");
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("target")]
    public async Task ExecuteAsync_AddToolOrTarget_PersistsDefinitionWithoutGrantingAgentAuthority(string kind)
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WriteManifestAsync(path);
        var resolver = new FixedManifestResolver(path);
        var result = kind == "tool"
            ? await new WorkspaceAddToolCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
                new WorkspaceAddToolOptions("edit-test", "new-tool", "mcp"), TestContext.Current.CancellationToken)
            : await new WorkspaceAddTargetCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
                new WorkspaceAddTargetOptions("edit-test", "new-target", "docker"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var manifest = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Agents.Keys.ShouldBe(["existing"]);
        manifest.Agents["existing"].Tools.ShouldBe(["existing-tool"]);
        manifest.Agents["existing"].Capabilities.ShouldBe(["tool:existing-tool:invoke:read"]);
        manifest.Tools["existing-tool"].Type.ShouldBe("filesystem");
        manifest.Targets["existing-target"].Runtime.ShouldBe("podman");
        if (kind == "tool")
        {
            manifest.Tools.Keys.Order().ShouldBe(["existing-tool", "new-tool"]);
            manifest.Tools["new-tool"].Type.ShouldBe("mcp");
            manifest.Targets.Keys.ShouldBe(["existing-target"]);
        }
        else
        {
            manifest.Targets.Keys.Order().ShouldBe(["existing-target", "new-target"]);
            manifest.Targets["new-target"].Runtime.ShouldBe("docker");
            manifest.Tools.Keys.ShouldBe(["existing-tool"]);
        }
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("tool")]
    [InlineData("target")]
    public async Task ExecuteAsync_DuplicateEntry_PreservesManifestBytes(string kind)
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WriteManifestAsync(path);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var resolver = new FixedManifestResolver(path);

        var result = await ExecuteExistingAsync(kind, resolver, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
        Directory.Exists(Path.Join(directory.Root, "prompts")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("tool")]
    [InlineData("target")]
    public async Task ExecuteAsync_MissingManifest_ReturnsFailure(string kind)
    {
        var result = await ExecuteExistingAsync(kind, new FixedManifestResolver(null), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
    }

    private static Task<int> ExecuteExistingAsync(string kind, FixedManifestResolver resolver, CancellationToken ct) => kind switch
    {
        "agent" => new WorkspaceAddAgentCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
            new WorkspaceAddAgentOptions("edit-test", "existing", "replacement-model"), ct),
        "tool" => new WorkspaceAddToolCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
            new WorkspaceAddToolOptions("edit-test", "existing-tool", "mcp"), ct),
        "target" => new WorkspaceAddTargetCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
            new WorkspaceAddTargetOptions("edit-test", "existing-target", "docker"), ct),
        _ => throw new ArgumentException("Unknown test command.", nameof(kind))
    };

    private static WorkspacePrompt Prompt(IManifestResolver resolver) => new(new RecordingWorkspaceRegistry(), resolver);

    private static Task WriteManifestAsync(string path) => File.WriteAllTextAsync(path,
        """{"version":"1.0","name":"edit-test","agents":{"existing":{"model":"preserved-model","tools":["existing-tool"],"capabilities":["tool:existing-tool:invoke:read"]}},"tools":{"existing-tool":{"type":"filesystem"}},"targets":{"existing-target":{"runtime":"podman"}}}""",
        TestContext.Current.CancellationToken);
}
