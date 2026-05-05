using Weave.Shared.Plugins;
using Weave.Silo.Security;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Plugins;

public sealed partial class AuthPluginConnector(
    PluginServiceBroker broker,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly ILogger<AuthPluginConnector> _logger = loggerFactory.CreateLogger<AuthPluginConnector>();

    public string PluginType => "auth";

    public PluginSchema Schema { get; } = new()
    {
        Type = "auth",
        Description = "API authentication — pluggable identity providers (apikey, bearer, or custom)",
        Provides = ["authentication"],
        Config =
        [
            new() { Name = "provider", Description = "Auth provider: apikey, bearer", Required = true },
            new() { Name = "secret", Description = "Auth secret (use env:, file:, or vault: reference)", Secret = true, EnvVar = "WEAVE_API_SECRET" },
        ]
    };

    public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var providerName = definition.Config.GetValueOrDefault("provider")?.ToLowerInvariant();
        var secret = definition.Config.GetValueOrDefault("secret");

        string? resolvedSecret = null;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            try
            {
                resolvedSecret = ResolveSecret(secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                LogAuthSecretResolutionFailed(ex, name);
                return Task.FromResult(new PluginStatus
                {
                    Name = name,
                    Type = PluginType,
                    IsConnected = false,
                    Error = $"Failed to resolve auth secret: {ex.Message}"
                });
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedSecret) && providerName is "apikey" or "bearer")
        {
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "Auth secret is required for apikey/bearer providers. Use env:, file:, or vault: reference."
            });
        }

        IApiAuthProvider provider = providerName switch
        {
            "apikey" => new ApiKeyAuthProvider(resolvedSecret!),
            "bearer" => new BearerAuthProvider(resolvedSecret!),
            _ => throw new InvalidOperationException(
                $"Unknown auth provider '{providerName}'. Built-in: apikey, bearer. " +
                "Register a custom IApiAuthProvider for enterprise providers (EntraID, Auth0, Keycloak, etc).")
        };

        broker.Swap<IApiAuthProvider>(provider);
        LogAuthConnected(name, providerName);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string>
            {
                ["provider"] = providerName,
                ["secret"] = "***"
            }
        });
    }

    public Task<PluginStatus> DisconnectAsync(string name)
    {
        broker.Swap<IApiAuthProvider>(null);
        LogAuthDisconnected(name);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = false
        });
    }

    public PluginStatus GetStatus(string name)
    {
        var active = broker.Get<IApiAuthProvider>();
        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = active is not null,
            Info = active is not null
                ? new Dictionary<string, string> { ["provider"] = active.Name }
                : new Dictionary<string, string>()
        };
    }

    private static string? ResolveSecret(string reference)
    {
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var varName = reference[4..].Trim();
            return Environment.GetEnvironmentVariable(varName);
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = reference[5..].Trim();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        return reference;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth plugin '{Name}' connected: provider={Provider}")]
    private partial void LogAuthConnected(string name, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth plugin '{Name}' disconnected")]
    private partial void LogAuthDisconnected(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth plugin '{Name}' failed to resolve secret reference")]
    private partial void LogAuthSecretResolutionFailed(Exception ex, string name);
}
