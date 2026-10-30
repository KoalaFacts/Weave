using System.Text.Json;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

public sealed class WorkspaceManifestFileTests
{
    [Fact]
    public async Task WriteAsync_Manifest_RoundTripsWithoutRewritingStoredPromptPath()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Documents, "workspace.json");
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "file-roundtrip",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new() { Model = "test-model", SystemPromptFile = "prompts/assistant.md", Tools = ["files"] }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["files"] = new() { Type = "filesystem" }
            }
        };

        await WorkspaceManifestFile.WriteAsync(path, manifest, TestContext.Current.CancellationToken);
        var loaded = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        var prepared = await WorkspaceManifestFile.ReadPreparedAsync(path, TestContext.Current.CancellationToken);

        loaded.Name.ShouldBe("file-roundtrip");
        loaded.Version.ShouldBe("1.0");
        loaded.Agents["assistant"].Model.ShouldBe("test-model");
        loaded.Agents["assistant"].Tools.ShouldBe(["files"]);
        loaded.Tools["files"].Type.ShouldBe("filesystem");
        loaded.Agents["assistant"].SystemPromptFile.ShouldBe("prompts/assistant.md");
        prepared.Agents["assistant"].SystemPromptFile.ShouldBe(Path.GetFullPath(Path.Join(directory.Documents, "prompts/assistant.md")));
        (await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken))
            .Agents["assistant"].SystemPromptFile.ShouldBe("prompts/assistant.md");
    }

    [Fact]
    public async Task ReadAsync_InvalidJson_PropagatesParseFailure()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await File.WriteAllTextAsync(path, "{ invalid", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<JsonException>(() => WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadPreparedAsync_Cancelled_DoesNotReturnManifest()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await File.WriteAllTextAsync(path, """{"version":"1.0","name":"cancelled"}""", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => WorkspaceManifestFile.ReadPreparedAsync(path, cancellation.Token));
    }
}
