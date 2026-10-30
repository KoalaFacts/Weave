using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class DataExportCliCommandTests
{
    [Fact]
    public async Task ExecuteAsync_PopulatedWorkspace_PreservesFilesAndAllLiveCollections()
    {
        using var fixture = new DataTransferFixture();
        const string manifest = """{"version":"1.0","name":"source-workspace"}""";
        await File.WriteAllTextAsync(fixture.ManifestPath, manifest, TestContext.Current.CancellationToken);
        var prompts = Path.Join(fixture.Root, "prompts");
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Join(prompts, "assistant.md"), "# Export marker\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(prompts, "ignored.txt"), "not a prompt", TestContext.Current.CancellationToken);
        var state = WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(state).ShouldNotBeNull());
        await File.WriteAllTextAsync(state, "  live-workspace\n", TestContext.Current.CancellationToken);
        fixture.Respond = path => DataTransferFixture.Response(200, path switch
        {
            "/health" => "",
            "/api/workspaces/live-workspace/skills" => """[{"marker":"skill","nested":{"count":3}}]""",
            "/api/workspaces/live-workspace/channels" => """[{"marker":"channel"}]""",
            "/api/marketplace" => """[{"marker":"marketplace"}]""",
            "/api/templates" => """[{"marker":"template"}]""",
            _ => throw new InvalidOperationException(path)
        });

        var result = await fixture.ExportCommand().ExecuteAsync(new DataExportOptions(null, fixture.OutputPath),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var exported = await fixture.ReadExportAsync();
        exported.WorkspaceName.ShouldBe("source-workspace");
        exported.WorkspaceId.ShouldBe("live-workspace");
        exported.Manifest.ShouldBe(manifest);
        exported.PromptFiles.Keys.ShouldBe(["assistant.md"]);
        exported.PromptFiles["assistant.md"].ShouldBe("# Export marker\n");
        exported.Skills.ShouldHaveSingleItem().GetProperty("nested").GetProperty("count").GetInt32().ShouldBe(3);
        exported.Channels.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("channel");
        exported.MarketplaceItems.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("marketplace");
        exported.Templates.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("template");
        fixture.Requests.ShouldAllBe(request => request.Method == "GET");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \n ")]
    public async Task ExecuteAsync_NoLiveWorkspaceId_ExportsGlobalDataWithoutWorkspaceQueries(string? stateText)
    {
        using var fixture = new DataTransferFixture();
        await File.WriteAllTextAsync(fixture.ManifestPath, "{}", TestContext.Current.CancellationToken);
        if (stateText is not null)
        {
            var state = WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(state).ShouldNotBeNull());
            await File.WriteAllTextAsync(state, stateText, TestContext.Current.CancellationToken);
        }
        fixture.Respond = path => DataTransferFixture.Response(200, path switch
        {
            "/health" => "",
            "/api/marketplace" => """[{"marker":"global-market"}]""",
            "/api/templates" => """[{"marker":"global-template"}]""",
            _ => throw new InvalidOperationException("Unexpected workspace query: " + path)
        });

        (await fixture.ExportCommand().ExecuteAsync(new DataExportOptions("source", fixture.OutputPath),
            TestContext.Current.CancellationToken)).ShouldBe(0);

        var exported = await fixture.ReadExportAsync();
        exported.WorkspaceId.ShouldBeNull();
        exported.Skills.ShouldBeEmpty();
        exported.Channels.ShouldBeEmpty();
        exported.PromptFiles.ShouldBeEmpty();
        exported.MarketplaceItems.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("global-market");
        exported.Templates.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("global-template");
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("channels")]
    [InlineData("marketplace")]
    [InlineData("templates")]
    public async Task ExecuteAsync_CollectionUnavailable_RetainsOtherCollections(string unavailable)
    {
        using var fixture = new DataTransferFixture();
        await File.WriteAllTextAsync(fixture.ManifestPath, "{}", TestContext.Current.CancellationToken);
        var state = WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(state).ShouldNotBeNull());
        await File.WriteAllTextAsync(state, "live", TestContext.Current.CancellationToken);
        fixture.Respond = path => path == "/health"
            ? DataTransferFixture.Response(200)
            : path.EndsWith("/" + unavailable, StringComparison.Ordinal)
                ? DataTransferFixture.Response(503)
                : DataTransferFixture.Response(200, """[{"marker":"retained"}]""");

        (await fixture.ExportCommand().ExecuteAsync(new DataExportOptions("source", fixture.OutputPath),
            TestContext.Current.CancellationToken)).ShouldBe(0);

        var exported = await fixture.ReadExportAsync();
        var collections = new Dictionary<string, IReadOnlyList<System.Text.Json.JsonElement>>
        {
            ["skills"] = exported.Skills,
            ["channels"] = exported.Channels,
            ["marketplace"] = exported.MarketplaceItems,
            ["templates"] = exported.Templates
        };
        foreach (var (name, values) in collections)
        {
            if (name == unavailable)
                values.ShouldBeEmpty();
            else
                values.ShouldHaveSingleItem().GetProperty("marker").GetString().ShouldBe("retained");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_MissingManifestOrUnavailableServer_DoesNotWriteSnapshot(bool missingManifest)
    {
        using var fixture = new DataTransferFixture();
        if (missingManifest)
            fixture.Resolver.Path = null;
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(503)
            : throw new InvalidOperationException(path);

        var result = await fixture.ExportCommand().ExecuteAsync(new DataExportOptions("source", fixture.OutputPath),
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        File.Exists(fixture.OutputPath).ShouldBeFalse();
        if (missingManifest)
            fixture.Requests.ShouldBeEmpty();
        else
            fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe("/health");
    }
}
