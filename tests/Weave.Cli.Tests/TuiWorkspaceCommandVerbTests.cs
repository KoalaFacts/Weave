using Weave.Actions.Agent;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiWorkspaceCommandVerbTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_NoWorkspace_ShowsOpenHintWithoutResolvingManifest(bool validate)
    {
        using var context = new TuiViewTestContext();
        var resolver = new CommandResolver(context.ManifestPath);
        var verb = CreateVerb(context, resolver, validate);

        await verb.DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No workspace open. Try: /open <workspace>");
        resolver.RequestedNames.ShouldBeEmpty();
        context.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_OpenWorkspace_UsesSessionNameAndRendersCommandResult(bool validate)
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");
        context.Respond = _ => TuiViewTestContext.Json("""{"name":"validated-review","agentCount":1,"toolCount":1,"targetCount":0,"errors":[]}""");
        var resolver = new CommandResolver(context.ManifestPath);
        var verb = CreateVerb(context, resolver, validate);
        var dispatched = new TuiVerbContext(context.Session, "ignored-other-workspace", () => throw new InvalidOperationException("Read-only verb cleared history"));

        await verb.DispatchAsync(dispatched, TestContext.Current.CancellationToken);

        resolver.RequestedNames.ShouldBe(["review-space"]);
        if (validate)
        {
            context.Requests.ShouldBe(["/api/workspaces/validate"]);
            context.Output.ShouldContain("Configuration valid.");
            context.Output.ShouldContain("validated-review");
        }
        else
        {
            context.Requests.ShouldBeEmpty();
            context.Output.ShouldContain("Agents (manifest)");
            context.Output.ShouldContain("review-model");
        }
        context.Session.AgentName.ShouldBe("reviewer");
        context.Session.WorkspaceName.ShouldBe("review-space");
    }

    private static ITuiVerb CreateVerb(TuiViewTestContext context, IManifestResolver resolver, bool validate)
    {
        var prompt = new WorkspacePrompt(new TuiViewTestContext.ReadOnlyRegistry(new Dictionary<string, string>()), resolver);
        return validate
            ? new ValidateVerb(new WorkspaceValidateCliCommand(new ValidateWorkspaceAction(context.Client), resolver, prompt))
            : new StatusVerb(new WorkspaceStatusCliCommand(new GetWorkspaceStatusAction(context.Client),
                new ListAgentsAction(context.Client), new ListToolsAction(context.Client), resolver, prompt));
    }

    private sealed class CommandResolver(string path) : IManifestResolver
    {
        public List<string?> RequestedNames { get; } = [];
        public string? Resolve(string? workspace)
        {
            RequestedNames.Add(workspace);
            return path;
        }
    }
}
