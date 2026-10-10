using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;

namespace Weave.Silo.Tests;

/// <summary>
/// Boots the real host, Orleans, CQRS dispatcher and HTTP pipeline. Actor state
/// uses memory; mandatory invocation recording uses a real, isolated SQLite file.
/// No external services, Docker or model credentials are required.
/// </summary>
public sealed class SiloFactory : WebApplicationFactory<Program>
{
    private static readonly JsonSerializerOptions CapabilityJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _journalDirectory = Path.Combine(Path.GetTempPath(), $"weave-host-journal-{Guid.NewGuid():N}");

    public static void Authorize(HttpClient client, IServiceProvider services, string workspaceId,
        params string[] grants)
    {
        var token = services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "silo-test-operator",
            Grants = new HashSet<string>(grants, StringComparer.Ordinal),
            Lifetime = TimeSpan.FromMinutes(5)
        });
        client.DefaultRequestHeaders.Remove("X-Weave-Capability");
        client.DefaultRequestHeaders.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(token, CapabilityJsonOptions)));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        SiloTestPorts.Configure(builder);
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

        // Keep Windows EventLog disposal from racing Orleans background logging.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_journalDirectory))
            Directory.Delete(_journalDirectory, recursive: true);
    }
}
