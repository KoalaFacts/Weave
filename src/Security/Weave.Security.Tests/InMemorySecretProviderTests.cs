using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Tests;

public sealed class InMemorySecretProviderTests
{
    private readonly CapabilityTokenService _tokenService = new();
    private readonly InMemorySecretProvider _provider;

    public InMemorySecretProviderTests()
    {
        _provider = new InMemorySecretProvider(_tokenService);
    }

    private CapabilityToken MintToken(
        string workspaceId = "ws-1",
        string issuedTo = "agent-1",
        HashSet<string>? grants = null,
        TimeSpan? lifetime = null) =>
        _tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = issuedTo,
            Grants = grants ?? ["secret:*"],
            Lifetime = lifetime ?? TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task ResolveAsync_WithValidTokenAndSecret_ReturnsValue()
    {
        _provider.SetSecret("api_key", "my-secret");
        var token = MintToken();

        var result = await _provider.ResolveAsync("api_key", token);

        result.DecryptToString().ShouldBe("my-secret");
    }

    [Fact]
    public async Task ResolveAsync_WithExpiredToken_ThrowsUnauthorized()
    {
        _provider.SetSecret("api_key", "my-secret");
        var token = MintToken(lifetime: TimeSpan.FromMilliseconds(-1));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _provider.ResolveAsync("api_key", token));
    }

    [Fact]
    public async Task ResolveAsync_WithInsufficientGrant_ThrowsUnauthorized()
    {
        _provider.SetSecret("api_key", "my-secret");
        var token = MintToken(grants: ["secret:other_key"]);

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _provider.ResolveAsync("api_key", token));
        ex.Message.ShouldContain("api_key");
    }

    [Fact]
    public async Task ResolveAsync_WithSpecificGrant_Succeeds()
    {
        _provider.SetSecret("api_key", "my-secret");
        var token = MintToken(grants: ["secret:api_key"]);

        var result = await _provider.ResolveAsync("api_key", token);

        result.DecryptToString().ShouldBe("my-secret");
    }

    [Fact]
    public async Task ResolveAsync_WithMissingSecret_ThrowsKeyNotFound()
    {
        var token = MintToken();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _provider.ResolveAsync("nonexistent", token));
    }

    [Fact]
    public async Task SetSecret_OverwritesExistingValue()
    {
        _provider.SetSecret("key", "original");
        _provider.SetSecret("key", "updated");
        var token = MintToken();

        var result = await _provider.ResolveAsync("key", token);

        result.DecryptToString().ShouldBe("updated");
    }

    [Fact]
    public async Task ListPathsAsync_ReturnsAllRegisteredPaths()
    {
        _provider.SetSecret("key1", "value1");
        _provider.SetSecret("key2", "value2");
        _provider.SetSecret("key3", "value3");

        var paths = await _provider.ListPathsAsync("ws-1");

        paths.Count.ShouldBe(3);
        paths.ShouldContain("key1");
        paths.ShouldContain("key2");
        paths.ShouldContain("key3");
    }

    [Fact]
    public async Task ListPathsAsync_WithNoSecrets_ReturnsEmpty()
    {
        var paths = await _provider.ListPathsAsync("ws-1");

        paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WithRevokedToken_ThrowsUnauthorized()
    {
        _provider.SetSecret("api_key", "my-secret");
        var token = MintToken();
        _tokenService.Revoke(token.TokenId);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _provider.ResolveAsync("api_key", token));
    }
}
