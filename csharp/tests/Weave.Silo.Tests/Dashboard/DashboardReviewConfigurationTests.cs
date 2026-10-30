using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Weave.Silo.Tests.Invocations;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardReviewConfigurationTests
{
    [Fact]
    public void Read_DisabledReview_DoesNotAcceptOrRequireAnUpstream()
    {
        var configuration = Configuration(false, "http://unsafe-external.test/path?credential=private-marker");

        dynamic options = Options(configuration);

        ((bool)options.Enabled).ShouldBeFalse();
        ((Uri?)options.Upstream).ShouldBeNull();
    }

    [Theory]
    [InlineData("https://review.weave.test/")]
    [InlineData("http://127.0.0.1:9411/")]
    public void Read_EnabledReview_UsesTrustedOrigin(string origin)
    {
        dynamic options = Options(Configuration(true, origin));

        ((bool)options.Enabled).ShouldBeTrue();
        ((Uri)options.Upstream).ShouldBe(new Uri(origin));
    }

    [Fact]
    public void AddApprovalReviewScreen_EnabledWithoutOrigin_RejectsRegistration()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(true, null);

        var error = Should.Throw<TargetInvocationException>(() => Register(services, configuration));

        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message
            .ShouldContain("Review requires a fixed HTTPS origin");
    }

    [Fact]
    public async Task AddApprovalReviewScreen_DifferentScopes_IsolatesSessionStateAndRetainsProvidedClock()
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        Register(services, Configuration(true, "https://review.weave.test/"));
        await using var provider = services.BuildServiceProvider();
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var sessionType = DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewSession");
        dynamic first = firstScope.ServiceProvider.GetRequiredService(sessionType);
        dynamic second = secondScope.ServiceProvider.GetRequiredService(sessionType);

        await first.LoadAsync("workspace", "files", "{invalid", "test-capability", "", TestContext.Current.CancellationToken);

        ((string?)first.Error).ShouldNotBeNull().ShouldContain("complete original invocation JSON");
        ((string?)second.Error).ShouldBeNull();
        ((object?)second.Snapshot).ShouldBeNull();
        ((object)first).ShouldBeSameAs(firstScope.ServiceProvider.GetRequiredService(sessionType));
        ((object)first).ShouldNotBeSameAs((object)second);
        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
    }

    private static IConfiguration Configuration(bool enabled, string? upstream) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Weave:Review:Enabled"] = enabled.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Weave:Review:BaseUrl"] = upstream
        }).Build();

    private static object Options(IConfiguration configuration) =>
        DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewOptions")
            .GetMethod("Read").ShouldNotBeNull().Invoke(null, [configuration]).ShouldNotBeNull();

    private static void Register(IServiceCollection services, IConfiguration configuration) =>
        DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ExtensionsToApprovalReview")
            .GetMethod("AddApprovalReviewScreen").ShouldNotBeNull().Invoke(null, [services, configuration]);
}
