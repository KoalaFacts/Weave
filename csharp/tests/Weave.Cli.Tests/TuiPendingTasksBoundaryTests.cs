using Weave.Actions.AgentTask;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiPendingTasksBoundaryTests
{
    [Fact]
    public async Task Tasks_PendingAndUntrustedStatus_RenderLiteralTaskDetailsForSelectedAgent()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => TuiViewTestContext.Json("""
            [
              {"taskId":"task[pending]","description":"Await [approval]","status":"Pending","createdAt":"2026-10-01T10:20:00Z"},
              {"taskId":"task[remote]","description":"Remote [literal] state","status":"[red]Pending[/]","createdAt":"2026-10-01T10:21:00Z"}
            ]
            """);

        await new TuiTasksView(new ListTasksAction(context.Client))
            .DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Requests.ShouldBe(["/api/workspaces/workspace-42/agents/reviewer/tasks"]);
        context.Output.ShouldContain("reviewer");
        context.Output.ShouldContain("task[pending]");
        context.Output.ShouldContain("Await [approval]");
        context.Output.ShouldContain("Pending");
        context.Output.ShouldContain("task[remote]");
        context.Output.ShouldContain("Remote [literal] state");
        context.Output.ShouldContain("[red]Pending[/]");
        context.Output.ShouldNotContain("Failed to fetch tasks");
    }
}
