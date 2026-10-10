using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspacePluginCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ListConfiguredPlugins_RendersDefinitionsWithoutChangingManifest()
    {
        using var output = new ShellOutputCapture();
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(path, Manifest(), TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var resolver = new FixedManifestResolver(path);
        var command = new WorkspacePluginListCliCommand(resolver, Prompt(resolver));

        (await command.ExecuteAsync(new WorkspaceNameOptions("plugin-workspace"), TestContext.Current.CancellationToken)).ShouldBe(0);

        output.Text.ShouldContain("remove-me");
        output.Text.ShouldContain("kept-plugin");
        output.Text.ShouldContain("config-marker=value-marker");
        output.Text.ShouldContain("retained-description");
        output.Text.ShouldContain("—");
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveNamedPlugin_RemovesOnlySelectedDefinitionAndPreservesAuthority()
    {
        using var output = new ShellOutputCapture();
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        var original = Manifest();
        await WorkspaceManifestFile.WriteAsync(path, original, TestContext.Current.CancellationToken);
        var resolver = new FixedManifestResolver(path);
        var command = new WorkspacePluginRemoveCliCommand(resolver, Prompt(resolver));

        (await command.ExecuteAsync(new WorkspacePluginRemoveOptions("plugin-workspace", "remove-me"),
            TestContext.Current.CancellationToken)).ShouldBe(0);

        var actual = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        actual.Plugins.Keys.ShouldBe(["kept-plugin"]);
        actual.Agents["assistant"].Capabilities.ShouldBe(["tool:documents:invoke:read"]);
        actual.Tools["documents"].Type.ShouldBe("filesystem");
        var expected = original with
        {
            Plugins = new Dictionary<string, PluginDefinition> { ["kept-plugin"] = original.Plugins["kept-plugin"] }
        };
        var parser = new ManifestParser();
        parser.Serialize(actual).ShouldBe(parser.Serialize(expected));
        output.Text.ShouldContain("Plugin 'remove-me' removed");
    }

    [Fact]
    public async Task ExecuteAsync_RemoveUnknownPlugin_PreservesManifestBytes()
    {
        using var output = new ShellOutputCapture();
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(path, Manifest(), TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var resolver = new FixedManifestResolver(path);
        var command = new WorkspacePluginRemoveCliCommand(resolver, Prompt(resolver));

        (await command.ExecuteAsync(new WorkspacePluginRemoveOptions("plugin-workspace", "unknown"),
            TestContext.Current.CancellationToken)).ShouldBe(1);

        output.Text.ShouldContain("Plugin 'unknown' not found");
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NoPlugins_DoesNotPromptOrRewriteManifest(bool remove)
    {
        using var output = new ShellOutputCapture();
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(path, Manifest() with { Plugins = [] }, TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var resolver = new FixedManifestResolver(path);

        (await ExecuteAsync(remove, resolver)).ShouldBe(0);

        output.Text.ShouldContain("No plugins configured");
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MissingManifest_ReturnsFailure(bool remove)
    {
        using var output = new ShellOutputCapture();

        (await ExecuteAsync(remove, new FixedManifestResolver(null))).ShouldBe(1);

        output.Text.ShouldContain("No workspace.json found");
    }

    private static Task<int> ExecuteAsync(bool remove, FixedManifestResolver resolver) => remove
        ? new WorkspacePluginRemoveCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
            new WorkspacePluginRemoveOptions("plugin-workspace", null), TestContext.Current.CancellationToken)
        : new WorkspacePluginListCliCommand(resolver, Prompt(resolver)).ExecuteAsync(
            new WorkspaceNameOptions("plugin-workspace"), TestContext.Current.CancellationToken);

    private static WorkspacePrompt Prompt(IManifestResolver resolver) => new(new RecordingWorkspaceRegistry(), resolver);

    private static WorkspaceManifest Manifest() => new()
    {
        Version = "1.0",
        Name = "plugin-workspace",
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["assistant"] = new() { Model = "retained-model", Tools = ["documents"], Capabilities = ["tool:documents:invoke:read"] }
        },
        Tools = new Dictionary<string, ToolDefinition> { ["documents"] = new() { Type = "filesystem" } },
        Plugins = new Dictionary<string, PluginDefinition>
        {
            ["remove-me"] = new() { Type = "custom" },
            ["kept-plugin"] = new()
            {
                Type = "http",
                Description = "retained-description",
                Config = new Dictionary<string, string> { ["config-marker"] = "value-marker" },
                Requires = new Dictionary<string, string> { ["dependency"] = "requirement-marker" },
                EnabledWhen = "flag-marker"
            }
        }
    };
}
