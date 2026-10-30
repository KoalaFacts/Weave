using System.Net;
using System.Text.RegularExpressions;

namespace Weave.Silo.Tests.Dashboard;

[Trait("Category", "Integration")]
public sealed partial class DashboardStylesheetTests
{
    [Fact]
    public async Task ReviewDocument_ScopedStylesheet_LoadsApplicationAndComponentStyles()
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartHostAsync();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = fixture.Address };

        using var page = await http.GetAsync("approvals/review", TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Read the actual served bundle before checking its document link, so a
        // missing build artifact cannot be mistaken for an unstyled page defect.
        using var sheet = await http.GetAsync("Weave.Dashboard.styles.css", TestContext.Current.CancellationToken);
        sheet.StatusCode.ShouldBe(HttpStatusCode.OK);
        sheet.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/css");
        var css = await sheet.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        css.ShouldContain(".review-page");
        var componentPath = ComponentImport().Match(css).Groups[1].Value;
        componentPath.ShouldStartWith("_content/Microsoft.FluentUI.AspNetCore.Components/");
        using var component = await http.GetAsync(componentPath, TestContext.Current.CancellationToken);
        component.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await component.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(".fluent-nav");

        html.ShouldContain("href=\"Weave.Dashboard.styles.css\" rel=\"stylesheet\"");
    }

    [GeneratedRegex("@import ['\"]([^'\"]+)['\"]")]
    private static partial Regex ComponentImport();
}
