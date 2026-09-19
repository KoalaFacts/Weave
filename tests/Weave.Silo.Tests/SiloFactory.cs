using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Weave.Silo.Tests;

/// <summary>
/// Boots the real Silo host (Program.cs) in-process for integration
/// tests. Uses local-mode defaults so we get:
///   - real Orleans cluster (UseLocalhostClustering)
///   - real InProcessRuntime (no Docker required)
///   - real Orleans in-memory storage (in-memory but production code)
///   - real CQRS dispatcher + all registered handlers
///   - real ASP.NET pipeline with every Silo endpoint
/// </summary>
/// <remarks>
/// Nothing is mocked. If the Silo fails to start — e.g. an Orleans
/// serializer config validator crash like the recent
/// CodecNotFoundException for AgentTaskId — these tests fail fast
/// instead of leaking through to the TUI.
/// </remarks>
public sealed class SiloFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Weave:LocalMode", "true");
        builder.UseSetting("Weave:Storage", "memory");

        // Bind to an ephemeral port so parallel fixtures don't collide.
        builder.UseSetting("urls", "http://127.0.0.1:0");

        builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // Silences the launchSettings prompt during tests.
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }));
    }
}
