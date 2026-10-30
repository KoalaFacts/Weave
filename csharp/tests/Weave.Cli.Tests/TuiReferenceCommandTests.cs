using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiReferenceCommandTests
{
    [Theory]
    [InlineData(9401, false)]
    [InlineData(12345, true)]
    public async Task DispatchAsync_Ports_RendersServiceMappingsAndOptionalOverride(int port, bool overridden)
    {
        using var context = new TuiViewTestContext();
        var store = new PortConfigStore(port);

        await new PortsVerb(new PortsCliCommand(store)).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Port Assignments");
        context.Output.Split('\n').Single(line => line.Contains("silo-http ", StringComparison.Ordinal)).ShouldContain("9401");
        context.Output.Split('\n').Single(line => line.Contains("dashboard-http ", StringComparison.Ordinal)).ShouldContain("9403");
        context.Output.ShouldContain("Orleans silo-to-silo clustering");
        context.Output.ShouldContain("Redis cache (standard default)");
        if (overridden)
            context.Output.ShouldContain("Config override: defaultPort = 12345");
        else
            context.Output.ShouldNotContain("Config override");
        context.Session.HasWorkspace.ShouldBeFalse();
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_Presets_ShowsAvailableTemplatesAndCreationInstructions()
    {
        using var context = new TuiViewTestContext();

        await new PresetsVerb(new WorkspacePresetsCliCommand()).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Presets");
        context.Output.ShouldContain("starter");
        context.Output.ShouldContain("coding-assistant");
        context.Output.ShouldContain("research");
        context.Output.ShouldContain("multi-agent");
        context.Output.ShouldContain("support-team");
        context.Output.ShouldContain("Model");
        context.Output.ShouldContain("Tools");
        context.Output.ShouldContain("A supervisor and worker assistants for complex workflows.");
        context.Output.ShouldContain("Use weave workspace new <name> --preset <preset> to create a workspace from a preset.");
        context.Session.HasWorkspace.ShouldBeFalse();
        context.Requests.ShouldBeEmpty();
    }

    private sealed class PortConfigStore(int port) : IConfigStore
    {
        public CliConfig Load() => new() { DefaultPort = port };
        public bool Exists() => true;
        public void Save(CliConfig config) => throw new InvalidOperationException("Read-only port command attempted config write");
    }
}
