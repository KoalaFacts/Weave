using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Weave.Silo.Tests;

/// <summary>
/// Boots the real host, Orleans, CQRS dispatcher and HTTP pipeline. Actor state
/// uses memory; mandatory invocation recording uses a real, isolated SQLite file.
/// No external services, Docker or model credentials are required.
/// </summary>
public sealed class SiloFactory : WebApplicationFactory<Program>
{
    private readonly string _journalDirectory = Path.Combine(Path.GetTempPath(), $"weave-host-journal-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Weave:LocalMode", "true");
        builder.UseSetting("Weave:Storage", "memory");
        builder.UseSetting("Weave:Invocations:DatabasePath", Path.Combine(_journalDirectory, "invocations.db"));

        // Bind to an ephemeral port so parallel fixtures don't collide.
        builder.UseSetting("urls", "http://127.0.0.1:0");

        builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_journalDirectory))
            Directory.Delete(_journalDirectory, recursive: true);
    }
}
