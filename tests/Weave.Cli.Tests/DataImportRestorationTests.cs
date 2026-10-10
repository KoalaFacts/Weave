using System.Text.Json.Nodes;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class DataImportRestorationTests
{
    [Fact]
    public async Task ExecuteAsync_CompleteSnapshot_RestoresFilesAndPostsDataToNewWorkspace()
    {
        using var fixture = new DataTransferFixture();
        var export = Snapshot();
        await fixture.WriteExportAsync(export);
        var capability = Path.Join(fixture.Root, "capability.txt");
        await File.WriteAllTextAsync(capability, "  encoded-token\n", TestContext.Current.CancellationToken);
        fixture.Respond = path => path switch
        {
            "/health" => DataTransferFixture.Response(200),
            "/api/workspaces" => Started(),
            "/api/workspaces/new-workspace/skills" or "/api/workspaces/new-workspace/channels" => DataTransferFixture.Response(201),
            _ => throw new InvalidOperationException(path)
        };

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination, capability), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        await AssertRestoredFilesAsync(fixture, export);
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, ".weave", "workspace-id"),
            TestContext.Current.CancellationToken)).ShouldBe("new-workspace");
        var startup = fixture.Requests.Single(request => request.Path == "/api/workspaces");
        startup.Method.ShouldBe("POST");
        startup.Capability.ShouldBe("encoded-token");
        startup.Body.ShouldNotBeNull()["manifest"].ShouldNotBeNull()["name"].ShouldNotBeNull().GetValue<string>().ShouldBe("snapshot-source");
        foreach (var collection in new[] { "skills", "channels" })
        {
            var request = fixture.Requests.Single(request => request.Path == "/api/workspaces/new-workspace/" + collection);
            request.Method.ShouldBe("POST");
            var expected = collection == "skills" ? export.Skills[0] : export.Channels[0];
            JsonNode.DeepEquals(request.Body, JsonNode.Parse(expected.GetRawText())).ShouldBeTrue();
        }
        fixture.Registry.Registrations.ShouldHaveSingleItem().ShouldBe(
            new KeyValuePair<string, string>(fixture.Destination, Path.GetFullPath(fixture.Destination)));
    }

    [Fact]
    public async Task ExecuteAsync_ChannelRestoreRejected_ReturnsFailureAndRetainsRestoredWorkspace()
    {
        using var fixture = new DataTransferFixture();
        var export = Snapshot();
        await fixture.WriteExportAsync(export);
        fixture.Respond = path => path switch
        {
            "/health" => DataTransferFixture.Response(200),
            "/api/workspaces" => Started(),
            "/api/workspaces/new-workspace/skills" => DataTransferFixture.Response(201),
            "/api/workspaces/new-workspace/channels" => DataTransferFixture.Response(403),
            _ => throw new InvalidOperationException(path)
        };

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        await AssertRestoredFilesAsync(fixture, export);
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, ".weave", "workspace-id"),
            TestContext.Current.CancellationToken)).ShouldBe("new-workspace");
        var channel = fixture.Requests.Single(request => request.Path.EndsWith("/channels", StringComparison.Ordinal));
        JsonNode.DeepEquals(channel.Body, JsonNode.Parse(export.Channels[0].GetRawText())).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnavailable_RestoresFilesWithoutClaimingRunningState()
    {
        using var fixture = new DataTransferFixture();
        var export = Snapshot();
        await fixture.WriteExportAsync(export);
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(503)
            : throw new InvalidOperationException(path);

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        await AssertRestoredFilesAsync(fixture, export);
        File.Exists(Path.Join(fixture.Destination, ".weave", "workspace-id")).ShouldBeFalse();
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe("/health");
        fixture.Registry.Registrations.ShouldHaveSingleItem().ShouldBe(
            new KeyValuePair<string, string>(fixture.Destination, Path.GetFullPath(fixture.Destination)));
    }

    [Fact]
    public async Task ExecuteAsync_StartCancelled_RetainsFilesWithoutRunningStateOrDataPosts()
    {
        using var fixture = new DataTransferFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var export = Snapshot();
        await fixture.WriteExportAsync(export);
        fixture.Respond = path =>
        {
            if (path == "/health")
                return DataTransferFixture.Response(200);
            if (path != "/api/workspaces")
                throw new InvalidOperationException(path);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination), cancellation.Token);

        result.ShouldBe(130);
        await AssertRestoredFilesAsync(fixture, export);
        File.Exists(Path.Join(fixture.Destination, ".weave", "workspace-id")).ShouldBeFalse();
        fixture.Requests.Select(request => request.Path).ShouldBe(["/health", "/api/workspaces"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("first-token\nsecond-token")]
    public async Task ExecuteAsync_UnreadableOrInvalidCapability_DoesNotRestoreOrContactServer(string? token)
    {
        using var fixture = new DataTransferFixture();
        await fixture.WriteExportAsync(Snapshot());
        var capability = Path.Join(fixture.Root, "capability.txt");
        if (token is not null)
            await File.WriteAllTextAsync(capability, token, TestContext.Current.CancellationToken);

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination, capability), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        Directory.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Requests.ShouldBeEmpty();
        fixture.Registry.Registrations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MissingOrNullSnapshot_DoesNotRestoreOrContactServer(bool nullSnapshot)
    {
        using var fixture = new DataTransferFixture();
        if (nullSnapshot)
            await File.WriteAllTextAsync(fixture.OutputPath, "null", TestContext.Current.CancellationToken);

        var result = await fixture.ImportCommand().ExecuteAsync(
            new DataImportOptions(fixture.OutputPath, fixture.Destination), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        Directory.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Requests.ShouldBeEmpty();
        fixture.Registry.Registrations.ShouldBeEmpty();
    }

    private static WorkspaceExport Snapshot() => new()
    {
        WorkspaceName = "snapshot-source",
        WorkspaceId = "old-workspace",
        Manifest = """{"version":"1.0","name":"snapshot-source"}""",
        PromptFiles = new Dictionary<string, string> { ["assistant.md"] = "# Restored prompt\nKeep this text.\n" },
        Skills = [DataTransferFixture.Element("""{"name":"skill-marker","nested":{"steps":[1,2]}}""")],
        Channels = [DataTransferFixture.Element("""{"name":"channel-marker","enabled":true}""")]
    };

    private static HttpResponseMessage Started() => DataTransferFixture.Response(201,
        """{"workspaceId":"new-workspace","name":"snapshot-source","status":"Running","recoveryCondition":"StartedOnThisHost","containerCount":0}""");

    private static async Task AssertRestoredFilesAsync(DataTransferFixture fixture, WorkspaceExport export)
    {
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "workspace.json"),
            TestContext.Current.CancellationToken)).ShouldBe(export.Manifest);
        (await File.ReadAllTextAsync(Path.Join(fixture.Destination, "prompts", "assistant.md"),
            TestContext.Current.CancellationToken)).ShouldBe(export.PromptFiles["assistant.md"]);
        Directory.Exists(Path.Join(fixture.Destination, "data")).ShouldBeTrue();
    }
}
