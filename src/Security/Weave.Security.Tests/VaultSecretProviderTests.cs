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
        Microsoft.Extensions.Options.Options.Create(_tokenOptions), TimeProvider.System);

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
            new CapabilityTokenService(Microsoft.Extensions.Options.Options.Create(_tokenOptions), TimeProvider.System),
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

    // --- ResolveAsync: HTTP 500 ---

    [Fact]
    public async Task ResolveAsync_VaultReturnsServerError_ThrowsHttpRequestException()
    {
        var handler = new StubHandler("internal server error", HttpStatusCode.InternalServerError);
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
    }

    // --- ResolveAsync: malformed JSON ---

    [Fact]
    public async Task ResolveAsync_VaultReturnsMalformedJson_ThrowsJsonException()
    {
        var handler = new StubHandler("not-json-at-all");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<System.Text.Json.JsonException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsMissingDataProperty_ThrowsKeyNotFoundException()
    {
        var handler = new StubHandler("""{"other":"stuff"}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
    }

    // --- ResolveAsync: expired token ---

    [Fact]
    public async Task ResolveAsync_ExpiredToken_ThrowsUnauthorized()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":"x"}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var expiredToken = _tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => provider.ResolveAsync("db-pass", expiredToken, TestContext.Current.CancellationToken));
    }

    // --- ResolveAsync: Vault 403 Forbidden ---

    [Fact]
    public async Task ResolveAsync_VaultReturnsForbidden_ThrowsHttpRequestException()
    {
        var handler = new StubHandler("permission denied", HttpStatusCode.Forbidden);
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("403");
    }

    // --- ResolveAsync: empty string value ---

    [Fact]
    public async Task ResolveAsync_VaultReturnsEmptyStringValue_ThrowsKeyNotFound()
    {
        var handler = new StubHandler("""{"data":{"data":{"value":""}}}""");
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => provider.ResolveAsync("empty-value", token, TestContext.Current.CancellationToken));
    }

    // --- ResolveAsync: path traversal is normalized by Uri ---

    [Fact]
    public async Task ResolveAsync_PathTraversalInSecretPath_NormalizedByUri()
    {
        // .NET's Uri class normalizes "../" segments, so path traversal
        // in the secret path is resolved before the HTTP request is sent.
        var handler = new StubHandler("not found", HttpStatusCode.NotFound);
        var provider = CreateProviderWithSharedTokenService(handler);
        var token = MintToken();

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.ResolveAsync("../../system/keys", token, TestContext.Current.CancellationToken));

        handler.LastRequestUri.ShouldNotBeNull();
        // Uri normalizes ".." segments — the literal traversal does not appear in the request
        handler.LastRequestUri!.PathAndQuery.ShouldNotContain("..");
    }

    [Fact]
    public async Task ResolveAsync_PathTraversalInWorkspaceId_NormalizedByUri()
    {
        var handler = new StubHandler("not found", HttpStatusCode.NotFound);
        var traversalToken = _tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "../other-workspace",
            IssuedTo = "agent-1",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var provider = CreateProviderWithSharedTokenService(handler);

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.ResolveAsync("db-pass", traversalToken, TestContext.Current.CancellationToken));

        handler.LastRequestUri.ShouldNotBeNull();
        // Uri normalizes ".." segments
        handler.LastRequestUri!.PathAndQuery.ShouldNotContain("..");
    }

    // --- ListPathsAsync: HTTP 500 ---

    [Fact]
    public async Task ListPathsAsync_VaultReturnsServerError_ThrowsHttpRequestException()
    {
        var handler = new StubHandler("error", HttpStatusCode.InternalServerError);
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
