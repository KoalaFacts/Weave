using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Weave.Silo.Tests;

/// <summary>
/// Smoke test: the Silo host must start cleanly. This is the thinnest
/// possible integration test — it catches any regression that prevents
/// the Silo from completing StartAsync (serializer config validator
/// failures, actor registration errors, DI resolution failures, etc).
/// </summary>
public sealed class BootTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public BootTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Silo_boots_and_health_check_returns_200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Start_TestHost_UsesConsoleWithoutWindowsEventLog()
    {
        using var client = _factory.CreateClient();
        var providers = _factory.Services.GetServices<ILoggerProvider>().ToArray();

        providers.ShouldContain(provider => provider.GetType().Name == "ConsoleLoggerProvider");
        providers.ShouldNotContain(provider => provider.GetType().Name == "EventLogLoggerProvider");
    }
}
