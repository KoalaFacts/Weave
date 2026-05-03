using Testcontainers.PostgreSql;

namespace Weave.Security.Tests;

/// <summary>
/// xUnit class fixture that boots a single Postgres container for the
/// integration test class. If Docker isn't available — local box without
/// Docker installed, restricted CI runner — <see cref="ConnectionString"/>
/// stays null and tests <see cref="Assert.Skip"/> with the captured reason.
///
/// Image is pinned to a specific tag so the suite is reproducible.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private const string ImageTag = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    /// <summary>Connection string for the running container, or null if Docker was unavailable.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Reason the container failed to start, surfaced to <see cref="Assert.Skip"/>.</summary>
    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
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
