using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Tests;

public sealed class VaultSecretProviderTests
{
    private static readonly CapabilityTokenOptions _tokenOptions = new() { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" };
    private readonly CapabilityTokenService _tokenService = new(
        Microsoft.Extensions.Options.Options.Create(_tokenOptions));

    private CapabilityToken MintToken(string grant = "secret:*") =>
        _tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = [grant]
        });

    private static VaultSecretProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://vault:8200") },
            new CapabilityTokenService(Microsoft.Extensions.Options.Options.Create(_tokenOptions)),
            Substitute.For<ILogger<VaultSecretProvider>>());

    private VaultSecretProvider CreateProviderWithSharedTokenService(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://vault:8200") },
            _tokenService,
            Substitute.For<ILogger<VaultSecretProvider>>());

    // --- ResolveAsync: success ---

    [Fact]
    public async Task ResolveAsync_ValidToken_ReturnsDecryptedSecret()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"my-secret-123"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        var result = await provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        result.DecryptToString().ShouldBe("my-secret-123");
    }

    [Fact]
    public async Task ResolveAsync_ValidToken_CallsCorrectVaultPath()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"x"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.PathAndQuery.ShouldBe("/v1/weave/ws-1/data/db-pass");
    }

    // --- ResolveAsync: token validation ---

    [Fact]
    public async Task ResolveAsync_TamperedToken_ThrowsUnauthorized()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"x"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);

        // Tamper with the signature to make validation fail
        var token = MintToken() with { Signature = "tampered-signature" };

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_TokenWithoutGrant_ThrowsUnauthorized()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"x"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);

        // Grant only for a specific path, not for "db-pass"
        var token = MintToken("secret:other-path");

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_WildcardGrant_Succeeds()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"ok"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken("secret:*");

        var result = await provider.ResolveAsync("any-path", token, TestContext.Current.CancellationToken);

        result.DecryptToString().ShouldBe("ok");
    }

    [Fact]
    public async Task ResolveAsync_SpecificGrant_Succeeds()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"specific"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken("secret:db-pass");

        var result = await provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        result.DecryptToString().ShouldBe("specific");
    }

    // --- ResolveAsync: Vault errors ---

    [Fact]
    public async Task ResolveAsync_VaultReturnsNotFound_ThrowsHttpRequestException()
    {
        var handler = new StubHandler("not found", HttpStatusCode.NotFound);
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.ResolveAsync("missing", token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsNullValue_ThrowsKeyNotFound()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":null}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => provider.ResolveAsync("empty", token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsMissingValueField_ThrowsKeyNotFound()
    {
        var handler = new StubHandler("""{"data":{"data":{"other":"field"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => provider.ResolveAsync("no-value-field", token, TestContext.Current.CancellationToken));
    }

    // --- ListPathsAsync ---

    [Fact]
    public async Task ListPathsAsync_ReturnsKeys()
    {
        var handler = new StubHandler("""{"data":{"keys":["secret-a","secret-b","secret-c"]}}""");
        var provider = CreateProviderWithSharedTokenService(handler);

        var paths = await provider.ListPathsAsync("ws-1", TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(3);
        paths.ShouldContain("secret-a");
        paths.ShouldContain("secret-b");
        paths.ShouldContain("secret-c");
    }

    [Fact]
    public async Task ListPathsAsync_CallsCorrectVaultPath()
    {
        var handler = new StubHandler("""{"data":{"keys":[]}}""");
        var provider = CreateProviderWithSharedTokenService(handler);

        await provider.ListPathsAsync("ws-42", TestContext.Current.CancellationToken);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.PathAndQuery.ShouldBe("/v1/weave/ws-42/metadata/?list=true");
    }

    [Fact]
    public async Task ListPathsAsync_EmptyKeys_ReturnsEmptyList()
    {
        var handler = new StubHandler("""{"data":{"keys":[]}}""");
        var provider = CreateProviderWithSharedTokenService(handler);

        var paths = await provider.ListPathsAsync("ws-1", TestContext.Current.CancellationToken);

        paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPathsAsync_VaultError_ThrowsHttpRequestException()
    {
        var handler = new StubHandler("forbidden", HttpStatusCode.Forbidden);
        var provider = CreateProviderWithSharedTokenService(handler);

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.ListPathsAsync("ws-1", TestContext.Current.CancellationToken));
    }

    // --- Stub handler ---

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
