using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Scalar.AspNetCore;
using Weave.Agents.Pipeline;
using Weave.Security.Plugins;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.ServiceDefaults;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.Plugins;
using Weave.Silo.Api;
using Weave.Silo.Configuration;
using Weave.Silo.Plugins;
using Weave.Silo.Security;
using Weave.Silo.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;
using Weave.Workspaces.Runtime;

var builder = WebApplication.CreateBuilder(args);
var weaveSettings = WeaveSettings.FromConfiguration(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SiloApiJsonContext.Default);
});

var isLocalMode = weaveSettings.IsLocalMode;

builder.AddServiceDefaults();

// Belt + suspenders: explicitly scan our own assembly for the
// [RegisterConverter] surrogates that adapt Weave.Shared types
// (branded IDs, DomainEvent, LifecycleContext, SecretValue) into
// Orleans wire format. The [ApplicationPart] attribute emitted by
// Microsoft.Orleans.Sdk usually handles this, but explicit scan
// guarantees the serializer config validator sees every converter
// at startup.
builder.Services.AddSerializer(s =>
    s.AddAssembly(typeof(Weave.Silo.Serialization.SerializationMarker).Assembly));

if (isLocalMode)
{
    builder.Services.AddOrleans(siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering();
        ConfigureActorStorage(siloBuilder, weaveSettings.ActorStorage, builder.Configuration);
    });
}
else
{
    builder.UseOrleans();
}

// Configures virtual actor storage based on Weave actor storage settings.
// Supported values: "memory" (default), "sqlite", "redis", "sqlserver", "postgresql"
static void ConfigureActorStorage(
    ISiloBuilder siloBuilder,
    WeaveSettings.ActorStorageSettings storageSettings,
    IConfiguration configuration)
{
    var storage = storageSettings.Provider.ToLowerInvariant();
    var schema = storageSettings.Schema;
    var database = storageSettings.Database;

    switch (storage)
    {
        case WeaveSettings.ActorStorageSettings.SqliteProvider:
            var sqliteConn = configuration.GetConnectionString(WeaveSettings.ActorStorageSettings.SqliteConnectionName)
                ?? DefaultSqlitePath();
            siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
            {
                options.ConnectionString = sqliteConn;
                options.Invariant = "Microsoft.Data.Sqlite";
            });
            break;

        case WeaveSettings.ActorStorageSettings.SqlServerProvider:
            var sqlConn = configuration.GetConnectionString(WeaveSettings.ActorStorageSettings.SqlServerConnectionName)
                ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required when Weave actor storage provider is 'sqlserver'.");
            if (!string.IsNullOrWhiteSpace(database))
                sqlConn = AppendIfMissing(sqlConn, $"Database={database}");
            else if (!string.IsNullOrWhiteSpace(schema))
                sqlConn = AppendIfMissing(sqlConn, $"Initial Catalog={schema}");
            siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
            {
                options.ConnectionString = sqlConn;
                options.Invariant = "Microsoft.Data.SqlClient";
            });
            siloBuilder.UseAdoNetClustering(options =>
            {
                options.ConnectionString = sqlConn;
                options.Invariant = "Microsoft.Data.SqlClient";
            });
            break;

        case WeaveSettings.ActorStorageSettings.PostgreSqlProvider or WeaveSettings.ActorStorageSettings.PostgresProvider:
            var pgConn = configuration.GetConnectionString(WeaveSettings.ActorStorageSettings.PostgreSqlConnectionName)
                ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required when Weave actor storage provider is 'postgresql'.");
            if (!string.IsNullOrWhiteSpace(database))
                pgConn = AppendIfMissing(pgConn, $"Database={database}");
            if (!string.IsNullOrWhiteSpace(schema))
                pgConn = AppendIfMissing(pgConn, $"SearchPath={schema}");
            siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
            {
                options.ConnectionString = pgConn;
                options.Invariant = "Npgsql";
            });
            siloBuilder.UseAdoNetClustering(options =>
            {
                options.ConnectionString = pgConn;
                options.Invariant = "Npgsql";
            });
            break;

        case WeaveSettings.ActorStorageSettings.RedisProvider:
            var redisConn = configuration.GetConnectionString(WeaveSettings.ActorStorageSettings.RedisConnectionName)
                ?? "localhost:6379";
            siloBuilder.AddRedisGrainStorageAsDefault(options =>
            {
                options.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConn);
            });
            break;

        default:
            siloBuilder.AddMemoryGrainStorageAsDefault();
            break;
    }
}

static string AppendIfMissing(string connectionString, string kvPair)
{
    var key = kvPair.Split('=')[0];
    if (connectionString.Contains(key, StringComparison.OrdinalIgnoreCase))
        return connectionString;
    return connectionString.TrimEnd(';') + ";" + kvPair;
}

static string DefaultSqlitePath()
{
    var weaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    return $"Data Source={Path.Combine(weaveHome, "weave.db")}";
}

// Shared kernel services
// TimeProvider is injected into every actor / service that needs a clock.
// Production pins the system clock; tests swap FakeTimeProvider via DI.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();
builder.Services.AddSingleton(weaveSettings);
builder.Services.AddSingleton<Weave.Shared.VirtualActors.IVirtualActorProvider, OrleansVirtualActorProvider>();

builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();

if (isLocalMode)
{
    builder.Services.AddSingleton<IWorkspaceRuntime, InProcessRuntime>();
}
else
{
    builder.Services.AddSingleton<IWorkspaceRuntime>(sp =>
        new ContainerRuntime(
            sp.GetRequiredService<ICommandRunner>(),
            new ContainerRuntimeOptions { Engine = weaveSettings.ContainerRuntime.Engine },
            sp.GetRequiredService<ILogger<ContainerRuntime>>()));
}

// Source-generated CQRS handler registration — no reflection
builder.Services.AddGeneratedCqrsHandlers();

// Security services
builder.Services.Configure<CapabilityTokenOptions>(
    builder.Configuration.GetSection(CapabilityTokenOptions.ConfigurationSectionName));
builder.Services.AddSingleton<ICapabilityTokenService, CapabilityTokenService>();
builder.Services.AddSingleton<ILeakScanner, LeakScanner>();
builder.Services.AddSingleton<TransparentSecretProxy>();

// --- Plugin hot-swap broker and proxy layer ---
// The broker holds mutable service slots; proxies delegate to the broker's current
// backing instance or fall back to defaults. This lets plugins swap implementations
// at runtime without rebuilding the DI container.
builder.Services.AddSingleton<PluginServiceBroker>();

// Default event bus (fallback when no plugin overrides it)
builder.Services.AddSingleton<InProcessEventBus>();
builder.Services.AddSingleton<IEventBus, EventBusProxy>();

// Default secret provider (fallback when no plugin overrides it)
builder.Services.AddSingleton<InMemorySecretProvider>();
builder.Services.AddSingleton<ISecretProvider>(sp =>
    new SecretProviderProxy(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<InMemorySecretProvider>()));

// Agent chat pipeline
builder.Services.AddSingleton<IAgentCostLedger, AgentCostLedger>();
builder.Services.AddScoped<IAgentChatClientFactory, AgentChatClientFactory>();
builder.Services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();

// Channel adapters
builder.Services.AddHttpClient<Weave.Silo.Channels.SlackChannelAdapter>();
builder.Services.AddSingleton<Weave.Agents.Channels.IChannelAdapter>(sp => sp.GetRequiredService<Weave.Silo.Channels.SlackChannelAdapter>());
builder.Services.AddHttpClient<Weave.Silo.Channels.DiscordChannelAdapter>();
builder.Services.AddSingleton<Weave.Agents.Channels.IChannelAdapter>(sp => sp.GetRequiredService<Weave.Silo.Channels.DiscordChannelAdapter>());
builder.Services.AddHttpClient<Weave.Silo.Channels.TelegramChannelAdapter>();
builder.Services.AddSingleton<Weave.Agents.Channels.IChannelAdapter>(sp => sp.GetRequiredService<Weave.Silo.Channels.TelegramChannelAdapter>());
builder.Services.AddHttpClient<Weave.Silo.Channels.TeamsChannelAdapter>();
builder.Services.AddSingleton<Weave.Agents.Channels.IChannelAdapter>(sp => sp.GetRequiredService<Weave.Silo.Channels.TeamsChannelAdapter>());
builder.Services.AddHttpClient<Weave.Silo.Channels.EmailChannelAdapter>();
builder.Services.AddSingleton<Weave.Agents.Channels.IChannelAdapter>(sp => sp.GetRequiredService<Weave.Silo.Channels.EmailChannelAdapter>());

// Tool connectors and discovery
builder.Services.AddSingleton<IToolConnector, McpToolConnector>();
builder.Services.AddSingleton<IToolConnector, CliToolConnector>();
builder.Services.AddSingleton<IToolConnector, FileSystemToolConnector>();
builder.Services.AddSingleton<IToolDiscoveryService, ToolDiscoveryService>();
builder.Services.AddHttpClient<OpenApiToolConnector>();
builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<OpenApiToolConnector>());
builder.Services.AddHttpClient<DirectHttpToolConnector>();
builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<DirectHttpToolConnector>());

// --- Plugin connectors (use broker + factories, not IServiceCollection) ---
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new DaprPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IToolDiscoveryService>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new VaultPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ICapabilityTokenService>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new HttpPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new AuthPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new WebhookPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();

// API security — opt-in authentication and audit logging
var authOptions = ApiAuthOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(authOptions);
var auditOptions = AuditOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(auditOptions);

builder.Services.AddOpenApi();

var app = builder.Build();

if (weaveSettings.RequireHttps)
    app.UseHttpsRedirection();

app.UseAuditLog(auditOptions);
app.UseApiAuth();

app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/problem+json";
    var problem = new ProblemDetails
    {
        Status = 500,
        Title = "Internal Server Error",
        Detail = "An unexpected error occurred."
    };
    await context.Response.WriteAsJsonAsync(problem, SiloApiJsonContext.Default.ProblemDetails);
}));

// --- Activate plugins from workspace manifest or environment ---
var pluginRegistry = app.Services.GetRequiredService<IPluginRegistry>();

// Environment-detected plugins (backward compat with DAPR_HTTP_PORT / Vault:Address)
if (weaveSettings.Dapr.HttpPort is not null)
{
    await pluginRegistry.ConnectAsync("dapr", new PluginDefinition
    {
        Type = "dapr",
        Description = "Auto-detected Dapr sidecar",
        Config = new Dictionary<string, string> { ["port"] = weaveSettings.Dapr.HttpPort }
    });
}

if (weaveSettings.Vault.Address is not null)
{
    var vaultConfig = new Dictionary<string, string> { ["address"] = weaveSettings.Vault.Address };
    if (weaveSettings.Vault.Token is not null)
        vaultConfig["token"] = weaveSettings.Vault.Token;

    await pluginRegistry.ConnectAsync("vault", new PluginDefinition
    {
        Type = "vault",
        Description = "Auto-detected Vault server",
        Config = vaultConfig
    });
}

// Log active plugins
foreach (var status in pluginRegistry.GetAll())
{
    if (status.IsConnected)
        app.Logger.LogInformation("Plugin '{Name}' ({Type}) active", status.Name, status.Type);
    else
        app.Logger.LogWarning("Plugin '{Name}' ({Type}) failed: {Error}", status.Name, status.Type, status.Error);
}

if (isLocalMode)
{
    app.Logger.LogInformation("Weave running in local mode — no external services required");
}

if (authOptions.Provider is not null)
    app.Logger.LogInformation("API authentication: {Mode}", authOptions.Mode);
else
    app.Logger.LogInformation("API authentication: disabled (opt in via Weave:Auth:Mode)");

if (auditOptions.Enabled)
    app.Logger.LogInformation("Audit logging: enabled");

if (weaveSettings.RequireHttps)
    app.Logger.LogInformation("HTTPS enforcement: enabled");

app.MapDefaultEndpoints();

// OpenAPI + Scalar
app.MapOpenApi();
app.MapScalarApiReference();

// Domain API
app.MapWorkspaceEndpoints();
app.MapAgentEndpoints();
app.MapToolEndpoints();
app.MapPluginEndpoints();
app.MapSkillEndpoints();
app.MapChannelEndpoints();
app.MapUserEndpoints();
app.MapMarketplaceEndpoints();
app.MapTemplateEndpoints();

app.Run();
