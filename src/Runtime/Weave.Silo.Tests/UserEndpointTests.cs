using System.Net;
using System.Net.Http.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end user profile endpoint tests — GET profile, PUT preference,
/// PUT domain context, DELETE clear, with validation paths on PUTs.
/// </summary>
public sealed class UserEndpointTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public UserEndpointTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetProfile_NewUser_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/users/user-1/profile",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPreference_EmptyKey_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PutAsJsonAsync(
            $"/api/workspaces/{ws}/users/user-1/preferences",
            new { Key = "", Value = "v" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetPreference_EmptyValue_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PutAsJsonAsync(
            $"/api/workspaces/{ws}/users/user-1/preferences",
            new { Key = "theme", Value = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetPreference_Valid_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PutAsJsonAsync(
            $"/api/workspaces/{ws}/users/user-1/preferences",
            new { Key = "theme", Value = "dark" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SetDomainContext_EmptyKey_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PutAsJsonAsync(
            $"/api/workspaces/{ws}/users/user-1/context",
            new { Key = "", Value = "v" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetDomainContext_Valid_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PutAsJsonAsync(
            $"/api/workspaces/{ws}/users/user-1/context",
            new { Key = "team", Value = "platform" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ClearProfile_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.DeleteAsync(
            $"/api/workspaces/{ws}/users/user-1",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
