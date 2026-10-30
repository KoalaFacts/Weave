using System.Security.Cryptography;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Silo.Invocations;

namespace Weave.Silo.Management;

internal sealed class ManagementAdmission(
    IManagementOperationJournal journal,
    ICapabilityTokenService tokens,
    TimeProvider timeProvider)
{
    public const string IdHeader = "X-Weave-Management-Id";

    public IResult? Admit(HttpContext context, string workspaceId, string action, string target,
        IReadOnlyList<string> authorizedGrants, byte[] requestBytes, out string id)
    {
        id = string.Empty;
        var invalidId = ReadId(context, out var requestId);
        if (invalidId is not null)
            return invalidId;
        if (!InvocationHttp.TryReadCapability(context, tokens, out var token, out var failure))
            return failure;

        id = requestId;
        var operation = new ManagementOperationRecord
        {
            Id = id,
            WorkspaceId = workspaceId,
            Subject = token.IssuedTo,
            TokenId = token.TokenId,
            Action = action,
            Target = target,
            AuthorizedGrants = string.Join(',', authorizedGrants.Order(StringComparer.Ordinal)),
            RequestDigest = Convert.ToHexStringLower(SHA256.HashData(requestBytes)),
            AdmittedAt = timeProvider.GetUtcNow()
        };
        if (!journal.TryAdmit(operation, context.RequestAborted))
            return InvocationHttp.Error(409, "management-operation-already-admitted");
        context.Response.Headers[IdHeader] = id;
        return null;
    }

    public static IResult? ReadId(HttpContext context, out string id)
    {
        id = string.Empty;
        var values = context.Request.Headers[IdHeader];
        Guid parsed;
        if (values.Count == 0)
            parsed = Guid.NewGuid();
        else if (values.Count != 1 || !Guid.TryParseExact(values[0], "N", out parsed) || parsed == Guid.Empty)
            return InvocationHttp.Error(400, "invalid-management-operation-id");
        id = parsed.ToString("N");
        return null;
    }

    public IResult Confirm(string id, IResult success)
    {
        return journal.Complete(id, ManagementOperationOutcome.Succeeded, timeProvider.GetUtcNow())
            ? success
            : InvocationHttp.Error(500, "management-outcome-unconfirmed");
    }

    public IResult ConfirmNoEffect(string id, IResult failure)
    {
        return journal.Complete(id, ManagementOperationOutcome.Failed, timeProvider.GetUtcNow())
            ? failure
            : InvocationHttp.Error(500, "management-outcome-unconfirmed");
    }
}
