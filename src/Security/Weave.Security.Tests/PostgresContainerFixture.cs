using Testcontainers.PostgreSql;

namespace Weave.Security.Tests;

/// <summary>
/// xUnit class fixture that boots a single Postgres container for the
/// integration test class.
///
/// Opt-in: tests stay skipped unless the <see cref="OptInEnvironmentVariable"/>
/// env var is set to a non-empty value. Default CI runners (where Docker
/// exists but the suite isn't intended to run real container tests) see
/// the integration tests as skipped, the same way they appear on a
/// Docker-less developer box. Set <c>WEAVE_RUN_POSTGRES_TESTS=1</c>
/// before <c>dotnet test</c> to enable them.
///
/// Image is pinned to a specific tag so the suite is reproducible.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    public const string OptInEnvironmentVariable = "WEAVE_RUN_POSTGRES_TESTS";
    private const string ImageTag = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    /// <summary>Connection string for the running container, or null if Docker was unavailable or the tests are not opted in.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Reason the container failed to start, surfaced to <see cref="Assert.Skip"/>.</summary>
    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptInEnvironmentVariable)))
        {
            UnavailableReason =
                $"Postgres integration tests are opt-in; set {OptInEnvironmentVariable}=1 to enable.";
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage(ImageTag)
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            // Docker not running, daemon socket missing, image pull blocked, etc.
            // Don't fail — let each test self-skip with a clear reason.
            UnavailableReason = $"Postgres container unavailable ({ex.GetType().Name}): {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
