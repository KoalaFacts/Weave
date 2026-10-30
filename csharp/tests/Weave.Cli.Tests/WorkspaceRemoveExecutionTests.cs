using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class WorkspaceRemoveExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RegisteredWorkspace_RemovesOnlySelectedRegistrationAndHonorsPurge(bool purge)
    {
        using var directory = new LocalTestDirectory();
        var removed = Path.Join(directory.Root, "removed");
        var retained = Path.Join(directory.Root, "retained");
        Directory.CreateDirectory(removed);
        Directory.CreateDirectory(retained);
        var removedFile = Path.Join(removed, "workspace.json");
        var retainedFile = Path.Join(retained, "workspace.json");
        await File.WriteAllTextAsync(removedFile, "removed-marker", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(retainedFile, "retained-marker", TestContext.Current.CancellationToken);
        var registry = new RecordingWorkspaceRegistry();
        registry.Register("removed", removed);
        registry.Register("retained", retained);
        var command = new WorkspaceRemoveCliCommand(registry, new WorkspacePrompt(registry, new FixedManifestResolver(null)));

        var result = await command.ExecuteAsync(new WorkspaceRemoveOptions("removed", purge), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        registry.Registrations.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("retained", retained));
        (await File.ReadAllTextAsync(retainedFile, TestContext.Current.CancellationToken)).ShouldBe("retained-marker");
        if (purge)
            Directory.Exists(removed).ShouldBeFalse();
        else
            (await File.ReadAllTextAsync(removedFile, TestContext.Current.CancellationToken)).ShouldBe("removed-marker");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownWorkspace_PreservesRegistrationAndFiles()
    {
        using var directory = new LocalTestDirectory();
        var registry = new RecordingWorkspaceRegistry();
        registry.Register("retained", directory.Root);
        var command = new WorkspaceRemoveCliCommand(registry, new WorkspacePrompt(registry, new FixedManifestResolver(null)));

        var result = await command.ExecuteAsync(new WorkspaceRemoveOptions("unknown", true), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        registry.Registrations.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("retained", directory.Root));
        (await File.ReadAllTextAsync(directory.Host, TestContext.Current.CancellationToken)).ShouldBe("fixture only; never executed");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRegistryWithoutName_ReturnsFailureWithoutPrompting()
    {
        var registry = new RecordingWorkspaceRegistry();
        var command = new WorkspaceRemoveCliCommand(registry, new WorkspacePrompt(registry, new FixedManifestResolver(null)));

        var result = await command.ExecuteAsync(new WorkspaceRemoveOptions(null, false), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        registry.Registrations.ShouldBeEmpty();
    }
}
