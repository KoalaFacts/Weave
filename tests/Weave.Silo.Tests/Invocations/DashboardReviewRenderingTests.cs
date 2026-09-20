using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Weave.Silo.Tests.Invocations;

public sealed class DashboardReviewRenderingTests
{
    [Fact]
    public async Task RenderReview_UntrustedContent_IsEncodedAndCannotBecomeActions()
    {
        var snapshot = Snapshot("<script>alert('body')</script>\n\t\u202Ehidden", "<img src=x onerror=alert(1)>");
        var html = await RenderAsync(snapshot, expired: false);

        html.ShouldContain("Verified snapshot");
        html.ShouldNotContain("<script>");
        html.ShouldNotContain("<img src=x");
        html.ShouldNotContain("\u202E");
        html.ShouldContain("202E", Case.Insensitive);
        html.ShouldContain("hidden");
        html.ShouldContain("No approval or execution");
        html.ShouldNotContain("<button");
    }

    [Fact]
    public async Task RenderReview_ExpiredSnapshot_IsNotPresentedAsCurrent()
    {
        var html = await RenderAsync(Snapshot("complete text", "operator-request"), expired: true);
        html.ShouldContain("Review expired");
        html.ShouldNotContain("Verified snapshot");
        html.ShouldContain("complete text");
        html.ShouldContain("Verify again");
    }

    [Fact]
    public void ReviewPage_HasExplicitRoute_WithoutApprovalAction()
    {
        var page = DashboardType("Weave.Dashboard.Pages.Approvals.Review");
        page.GetCustomAttributes<RouteAttribute>().ShouldContain(route => route.Template == "/approvals/review");
    }

    private static object Snapshot(string input, string subject)
    {
        var json = JsonSerializer.Serialize(new
        {
            InvocationId = "abcdef0123456789abcdef0123456789",
            WorkspaceId = "workspace",
            Subject = subject,
            ToolName = "files",
            Operation = "write_file",
            TargetDescription = "FileSystem; root=/controlled; sandbox=true",
            Parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            RawInput = input,
            PlanDigest = "test-only-plan-digest",
            ExpiresAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        });
        return JsonSerializer.Deserialize(json, DashboardType("Weave.Dashboard.Approvals.ApprovalReviewSnapshot"))!;
    }

    private static async Task<string> RenderAsync(object snapshot, bool expired)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync(
                DashboardType("Weave.Dashboard.Pages.Approvals.ApprovalReviewPanel"),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Snapshot"] = snapshot,
                    ["Expired"] = expired
                }));
            return component.ToHtmlString();
        });
    }

    // Load the real Dashboard built by the existing solution build. Reflection here
    // avoids a second web-host project dependency and changes to package lockfiles.
    // Do not silently skip if it was not built: run the documented full solution build.
    internal static Type DashboardType(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Weave.slnx")))
            root = root.Parent;
        root.ShouldNotBeNull("Run from a built repository checkout.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assemblyPath = Path.Combine(root.FullName, "hosts", "Weave.Dashboard", "bin", configuration,
            "net10.0", "Weave.Dashboard.dll");
        File.Exists(assemblyPath).ShouldBeTrue("Build Weave.slnx before running the Dashboard rendering tests.");
        return Assembly.LoadFrom(assemblyPath).GetType(name).ShouldNotBeNull(
            "The approved read-only review screen has not been implemented yet.");
    }
}
