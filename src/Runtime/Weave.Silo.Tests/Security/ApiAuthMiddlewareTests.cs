using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Weave.Shared.Plugins;
using Weave.Silo.Security;

namespace Weave.Silo.Tests.Security;

/// <summary>
/// Unit tests for ApiKeyAuthProvider, BearerAuthProvider, ApiAuthMiddleware,
/// and ApiAuthOptions.FromConfiguration. These run in-process without a
/// SiloFactory — the middleware is a pure function over HttpContext.
/// </summary>
public sealed class ApiAuthMiddlewareTests
{
    // --- ApiKeyAuthProvider ---

    [Fact]
    public async Task ApiKeyAuthProvider_ValidKey_ReturnsTrue()
    {
        var provider = new ApiKeyAuthProvider("secret-key");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "secret-key";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ApiKeyAuthProvider_MissingHeader_ReturnsFalse()
    {
        var provider = new ApiKeyAuthProvider("secret-key");
        var context = new DefaultHttpContext();

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ApiKeyAuthProvider_WrongKey_ReturnsFalse()
    {
        var provider = new ApiKeyAuthProvider("secret-key");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "not-the-right-key";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public void ApiKeyAuthProvider_Exposes_ProviderMetadata()
    {
        var provider = new ApiKeyAuthProvider("x");
        provider.Name.ShouldBe("apikey");
        provider.UnauthorizedMessage.ShouldContain("X-Api-Key");
    }

    // --- BearerAuthProvider ---

    [Fact]
    public async Task BearerAuthProvider_ValidBearer_ReturnsTrue()
    {
        var provider = new BearerAuthProvider("token-123");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token-123";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task BearerAuthProvider_CaseInsensitivePrefix_ReturnsTrue()
    {
        var provider = new BearerAuthProvider("token-123");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "bearer token-123";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task BearerAuthProvider_MissingHeader_ReturnsFalse()
    {
        var provider = new BearerAuthProvider("token-123");
        var context = new DefaultHttpContext();

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task BearerAuthProvider_NonBearerScheme_ReturnsFalse()
    {
        var provider = new BearerAuthProvider("token-123");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task BearerAuthProvider_WrongToken_ReturnsFalse()
    {
        var provider = new BearerAuthProvider("token-123");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer wrong-token";

        var result = await provider.AuthenticateAsync(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public void BearerAuthProvider_Exposes_ProviderMetadata()
    {
        var provider = new BearerAuthProvider("x");
        provider.Name.ShouldBe("bearer");
        provider.UnauthorizedMessage.ShouldContain("Bearer");
    }

    // --- ApiAuthMiddleware ---

    private static (ApiAuthMiddleware Middleware, TrackingNext Next, PluginServiceBroker Broker) CreateMiddleware(
        IApiAuthProvider? defaultProvider = null)
    {
        var next = new TrackingNext();
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var options = new ApiAuthOptions { Provider = defaultProvider, Mode = defaultProvider is null ? "none" : "apikey" };
        var mw = new ApiAuthMiddleware(next.Invoke, broker, options, NullLogger<ApiAuthMiddleware>.Instance);
        return (mw, next, broker);
    }

    [Fact]
    public async Task Middleware_NoProviderConfigured_PassesThrough()
    {
        var (mw, next, _) = CreateMiddleware(defaultProvider: null);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/workspaces";

        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Middleware_HealthPath_BypassesAuthEvenWithProvider()
    {
        var (mw, next, _) = CreateMiddleware(new ApiKeyAuthProvider("k"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/health";

        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Middleware_NonApiPath_BypassesAuth()
    {
        var (mw, next, _) = CreateMiddleware(new ApiKeyAuthProvider("k"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";

        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Middleware_AuthenticatedRequest_CallsNext()
    {
        var (mw, next, _) = CreateMiddleware(new ApiKeyAuthProvider("k"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/workspaces";
        ctx.Request.Headers["X-Api-Key"] = "k";

        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Middleware_UnauthenticatedRequest_Returns401ProblemDetails()
    {
        var (mw, next, _) = CreateMiddleware(new ApiKeyAuthProvider("k"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/workspaces";
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeFalse();
        ctx.Response.StatusCode.ShouldBe(401);
        // WriteAsJsonAsync overwrites the ContentType the middleware sets,
        // but the body itself is authoritative — assert on that.
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Unauthorized");
        body.ShouldContain("X-Api-Key");
    }

    [Fact]
    public async Task Middleware_BrokerProviderOverridesOptions()
    {
        var (mw, next, broker) = CreateMiddleware(defaultProvider: null);
        broker.Swap<IApiAuthProvider>(new ApiKeyAuthProvider("broker-key"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/ping";

        // Correct key from broker's provider
        ctx.Request.Headers["X-Api-Key"] = "broker-key";
        await mw.InvokeAsync(ctx);

        next.Invoked.ShouldBeTrue("broker-provided provider is used before the options default");
    }

    // --- ApiAuthOptions.FromConfiguration ---

    [Fact]
    public void FromConfiguration_ModeNone_ReturnsNullProvider()
    {
        var config = BuildConfig(new() { ["Weave:Auth:Mode"] = "none" });

        var options = ApiAuthOptions.FromConfiguration(config);

        options.Provider.ShouldBeNull();
        options.Mode.ShouldBe("none");
    }

    [Fact]
    public void FromConfiguration_ModeApiKey_InlineSecret_ReturnsApiKeyProvider()
    {
        var config = BuildConfig(new()
        {
            ["Weave:Auth:Mode"] = "apikey",
            ["Weave:Auth:Secret"] = "literal-secret"
        });

        var options = ApiAuthOptions.FromConfiguration(config);

        options.Provider.ShouldBeOfType<ApiKeyAuthProvider>();
    }

    [Fact]
    public void FromConfiguration_ModeBearer_InlineSecret_ReturnsBearerProvider()
    {
        var config = BuildConfig(new()
        {
            ["Weave:Auth:Mode"] = "bearer",
            ["Weave:Auth:Secret"] = "token"
        });

        var options = ApiAuthOptions.FromConfiguration(config);

        options.Provider.ShouldBeOfType<BearerAuthProvider>();
    }

    [Fact]
    public void FromConfiguration_EnvSecretReference_ResolvesFromEnvironment()
    {
        var envVar = $"WEAVE_TEST_AUTH_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "from-env");
        try
        {
            var config = BuildConfig(new()
            {
                ["Weave:Auth:Mode"] = "apikey",
                ["Weave:Auth:Secret"] = $"env:{envVar}"
            });

            var options = ApiAuthOptions.FromConfiguration(config);

            options.Provider.ShouldBeOfType<ApiKeyAuthProvider>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void FromConfiguration_FileSecretReference_ResolvesFromFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"weave-auth-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempPath, "from-file-secret");
        try
        {
            var config = BuildConfig(new()
            {
                ["Weave:Auth:Mode"] = "bearer",
                ["Weave:Auth:Secret"] = $"file:{tempPath}"
            });

            var options = ApiAuthOptions.FromConfiguration(config);

            options.Provider.ShouldBeOfType<BearerAuthProvider>();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void FromConfiguration_FileSecretReference_MissingFile_ResolvesToNoProvider()
    {
        var config = BuildConfig(new()
        {
            ["Weave:Auth:Mode"] = "apikey",
            ["Weave:Auth:Secret"] = "file:C:/does/not/exist/xyz.txt"
        });

        var options = ApiAuthOptions.FromConfiguration(config);

        options.Provider.ShouldBeNull();
    }

    [Fact]
    public void FromConfiguration_ModeNotEmpty_SecretEmpty_ReturnsNullProvider()
    {
        var config = BuildConfig(new()
        {
            ["Weave:Auth:Mode"] = "apikey",
            ["Weave:Auth:Secret"] = ""
        });

        var options = ApiAuthOptions.FromConfiguration(config);

        options.Provider.ShouldBeNull();
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TrackingNext
    {
        public bool Invoked { get; private set; }

        public Task Invoke(HttpContext _)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }
}
