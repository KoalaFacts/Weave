using Microsoft.Data.Sqlite;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

public static partial class MailboxHost
{
    public static WebApplication Create(MailboxHostOptions options, IExpiringMailboxStore store,
        TimeProvider timeProvider, MailboxControlAuthentication authentication,
        Action<WebApplicationBuilder>? configure = null)
    {
        Validate(options);
        authentication.Configure(options.Credentials);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = MailboxHttp.MaximumRequestBytes;
            server.Limits.MaxResponseBufferSize = 128 * 1024;
            server.Limits.MaxRequestHeadersTotalSize = 8192;
            server.Limits.MaxRequestHeaderCount = 32;
            server.Limits.MaxConcurrentConnections = 256;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        configure?.Invoke(builder);
        builder.Services.AddSingleton<IHostedService>(services => new MailboxCleanupService(store, options, timeProvider,
            services.GetRequiredService<ILogger<MailboxCleanupService>>()));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            var headers = context.Request.Headers[MailboxControlAuthentication.HeaderName];
            var scope = headers.Count == 1 ? authentication.Authenticate(headers[0]!) : null;
            if (headers.Count > 0 && scope is null) { await MailboxHttp.ErrorAsync(context, 401, "unauthenticated"); return; }
            if (scope is not null) context.Features.Set(scope);
            var publicDiscovery = context.GetEndpoint()?.Metadata.GetMetadata<PublicCardDiscovery>() is not null;
            if (scope is null && !publicDiscovery) { await MailboxHttp.ErrorAsync(context, 401, "unauthenticated"); return; }
            try { await next(context); }
            catch (SqliteException)
            {
                StorageUnavailable(app.Logger);
                if (context.Response.HasStarted) context.Abort();
                else await MailboxHttp.ErrorAsync(context, 503, "storage");
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            { if (!context.Response.HasStarted) await MailboxHttp.ErrorAsync(context, 413, "tooLarge"); }
        });
        new ContactEndpoints(store).Map(app);
        new MailboxEndpoints(store, new(store, options, authentication, timeProvider)).Map(app);
        return app;
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox request could not access storage.")]
    private static partial void StorageUnavailable(ILogger logger);

    private static void Validate(MailboxHostOptions options)
    {
        if (options.StreamLifetime <= TimeSpan.Zero || options.StreamLifetime > TimeSpan.FromMinutes(5)
            || options.StreamWriteTimeout <= TimeSpan.Zero || options.StreamWriteTimeout > TimeSpan.FromSeconds(30)
            || options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromSeconds(30)
            || options.MaximumStreamsPerMailbox is < 1 or > 2 || options.MaximumStreams is < 1 or > 100
            || options.CleanupInterval < TimeSpan.FromSeconds(1) || options.CleanupInterval > TimeSpan.FromMinutes(5)
            || options.CleanupBatchSize is < 1 or > 1000)
            throw new ArgumentException("Mailbox host bounds are invalid.", nameof(options));
    }
}
