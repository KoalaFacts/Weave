using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Weave.Security.Tokens;
using Weave.Silo.Invocations;

namespace Weave.Silo.Operator;

internal sealed class TrustedOperatorAccess
{
    private readonly byte[] _keyHash;
    public FrozenDictionary<string, TrustedOperatorTool> Tools { get; }
    public FrozenDictionary<string, CapabilityTokenRequest> Credentials { get; }

    public TrustedOperatorAccess(WebApplication app)
    {
        var configuration = app.Configuration;
        var section = configuration.GetSection("Weave:Operator");
        if (configuration.GetValue<bool>("Weave:Invocations:Http:AgentOnly")
            || !configuration.GetValue<bool>("Weave:Invocations:Http:Enabled"))
            throw new InvalidOperationException("Operator mode requires governed HTTP enabled and AgentOnly disabled.");
        var key = section["Key"];
        var signing = app.Services.GetRequiredService<IOptions<CapabilityTokenOptions>>().Value;
        if (key is null || !ValidKey(key) || key == signing.SigningKey || key == signing.PreviousSigningKey
            || key == configuration["Weave:Auth:Secret"])
            throw new InvalidOperationException("Configure a separate 32-256 character operator key; do not reuse signing or API credentials.");
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        var tools = section.GetSection("Tools").Get<Dictionary<string, TrustedOperatorTool>>() ?? [];
        var credentials = section.GetSection("Credentials").Get<Dictionary<string, CapabilityTokenRequest>>() ?? [];
        if (tools.Count is 0 or > 64 || credentials.Count is 0 or > 64)
            throw new InvalidOperationException("Operator mode requires 1-64 declared tools and credential profiles.");
        foreach (var (name, tool) in tools)
            if (!Segment(name) || tool is null || !Segment(tool.WorkspaceId) || tool.Tool is null
                || !Segment(tool.Tool.Name) || !Enum.IsDefined(tool.Tool.Type))
                throw new InvalidOperationException("An operator tool profile is invalid.");
        foreach (var (name, credential) in credentials)
            if (!Segment(name) || credential is null || !Segment(credential.WorkspaceId)
                || !Segment(credential.IssuedTo) || credential.Lifetime <= TimeSpan.Zero
                || credential.Lifetime > TimeSpan.FromHours(1) || credential.Grants is null
                || credential.Grants.Count is 0 or > 64
                || credential.Grants.Any(grant => string.IsNullOrWhiteSpace(grant)
                    || grant.Length > 256 || grant.Contains('*') || grant.Any(char.IsControl)))
                throw new InvalidOperationException("An operator credential profile is invalid; use explicit grants and a positive lifetime up to one hour.");
        Tools = tools.ToFrozenDictionary(StringComparer.Ordinal);
        Credentials = credentials.ToFrozenDictionary(pair => pair.Key,
            pair => pair.Value with { Grants = new HashSet<string>(pair.Value.Grants, StringComparer.Ordinal) }, StringComparer.Ordinal);
    }

    public async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        // Only endpoint metadata assigned by the Host exempts an Agent operation.
        // A route prefix, submitted workspace, Host or forwarded header is not authority.
        var health = HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path == "/health" || context.Request.Path == "/alive");
        if (health || context.GetEndpoint()?.Metadata.GetMetadata<AgentInvocationEndpoint>() is not null)
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var remote = context.Connection.RemoteIpAddress;
        if (!context.Request.IsHttps && (remote is null || !IPAddress.IsLoopback(remote)))
        {
            await InvocationHttp.Error(403, "operator-secure-transport-required").ExecuteAsync(context);
            return;
        }
        var values = context.Request.Headers["X-Weave-Operator-Key"];
        if (values.Count != 1 || values[0] is not { } key || !ValidKey(key)
            || !CryptographicOperations.FixedTimeEquals(_keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(key))))
        {
            await InvocationHttp.Error(401, "operator-authentication-required").ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static bool Segment(string? value) => value is not null && InvocationHttp.IsRouteSegment(value);
    private static bool ValidKey(string value) => value.Length is >= 32 and <= 256
        && value.All(character => character is >= '!' and <= '~');
}
