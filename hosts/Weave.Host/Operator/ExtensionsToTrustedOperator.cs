using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Invocations;
using Weave.Tools.Tool;

namespace Weave.Silo.Operator;

internal static class ExtensionsToTrustedOperator
{
    public static void ConfigureTrustedOperator(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Weave:Operator:Enabled"))
            return;
        var access = new TrustedOperatorAccess(app);
        app.Use(access.GuardAsync);
        var group = app.MapGroup("/api/operator").WithTags("Trusted Operator");
        group.MapPost("/tools/{profile}/connect", (HttpContext context, string profile,
            ICapabilityTokenService tokens, IVirtualActorProvider actors) => ConnectAsync(context, profile, access, tokens, actors));
        group.MapPost("/credentials/{profile}/issue", (HttpContext context, string profile,
            ICapabilityTokenService tokens) => Issue(context, profile, access, tokens));
    }

    private static async Task<IResult> ConnectAsync(HttpContext context, string profile, TrustedOperatorAccess access,
        ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (HasInput(context))
            return InvocationHttp.Error(400, "operator-profile-request-must-be-empty");
        if (!access.Tools.TryGetValue(profile, out var configured))
            return InvocationHttp.Error(404, "operator-profile-not-found");
        context.RequestAborted.ThrowIfCancellationRequested();
        var token = tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = configured.WorkspaceId,
            IssuedTo = "trusted-operator-setup",
            Grants = ["tool:" + configured.Tool.Name + ":connect"],
            Lifetime = TimeSpan.FromMinutes(1)
        }) with
        { CancellationToken = context.RequestAborted };
        if (!tokens.Validate(token))
            return InvocationHttp.Error(503, "operator-authority-unavailable");
        var actor = actors.GetActor<IToolActor>(VirtualActorId.From(configured.WorkspaceId + "/" + configured.Tool.Name));
        await actor.ConnectAsync(configured.Tool, token);
        return Results.NoContent();
    }

    private static IResult Issue(HttpContext context, string profile, TrustedOperatorAccess access, ICapabilityTokenService tokens)
    {
        if (HasInput(context))
            return InvocationHttp.Error(400, "operator-profile-request-must-be-empty");
        if (!access.Credentials.TryGetValue(profile, out var configured))
            return InvocationHttp.Error(404, "operator-profile-not-found");
        context.RequestAborted.ThrowIfCancellationRequested();
        var token = tokens.Mint(configured);
        if (!tokens.Validate(token))
            return InvocationHttp.Error(503, "operator-authority-unavailable");
        var encoded = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token,
            InvocationHttpJsonContext.Default.CapabilityToken));
        return Results.Text(encoded, "text/plain");
    }

    internal static bool HasInput(HttpContext context) => context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
        || context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
        || context.Request.QueryString.HasValue;
}
