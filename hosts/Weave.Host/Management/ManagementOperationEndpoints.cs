using Weave.Management;
using Weave.Security.Tokens;
using Weave.Silo.Invocations;
using Weave.Silo.Plugins;

namespace Weave.Silo.Management;

internal static class ManagementOperationEndpoints
{
    public static RouteGroupBuilder MapManagementOperationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/management/operations").WithTags("Management");
        group.MapGet("/{workspaceId}/{id}", GetAsync)
            .WithDescription("Read retained metadata for one management operation.")
            .Produces<ManagementOperationRecord>()
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404);
        return group;
    }

    private static async Task<IResult> GetAsync(string workspaceId, string id,
        HttpContext context, IPluginInstallationAuthority authority,
        IManagementOperationJournal journal, CancellationToken ct)
    {
        var denial = await authority.DenialAsync(context, workspaceId, "management:operations:read");
        if (denial is not null)
            return denial;
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty)
            return InvocationHttp.Error(400, "invalid-management-operation-id");
        var operation = journal.Find(parsed.ToString("N"), ct);
        return operation is not null && string.Equals(operation.WorkspaceId, workspaceId, StringComparison.Ordinal)
            ? Results.Ok(operation)
            : InvocationHttp.Error(404, "management-operation-not-found");
    }
}
