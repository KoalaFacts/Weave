using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Weave.Invocations;

namespace Weave.Silo.Invocations;

internal sealed class ApprovalPlanProtector : IApprovalPlanProtector
{
    private readonly IDataProtectionProvider _provider;

    public ApprovalPlanProtector(IOptions<InvocationJournalOptions> options)
    {
        var database = options.Value.DatabasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "invocations.db");
        var directory = new DirectoryInfo(Path.GetFullPath(database) + ".approval-keys");
        _provider = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Weave.Approvals.v0"));
    }

    public string Protect(string workspaceId, InvocationId id, string cleartext) =>
        For(workspaceId, id).Protect(cleartext);

    public string Unprotect(string workspaceId, InvocationId id, string ciphertext) =>
        For(workspaceId, id).Unprotect(ciphertext);

    private IDataProtector For(string workspace, InvocationId id) =>
        _provider.CreateProtector("FileWritePlan.v1", workspace, id.ToString());
}
