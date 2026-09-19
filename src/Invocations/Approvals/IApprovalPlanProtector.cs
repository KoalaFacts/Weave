namespace Weave.Invocations;

public interface IApprovalPlanProtector
{
    string Protect(string workspaceId, InvocationId id, string cleartext);
    string Unprotect(string workspaceId, InvocationId id, string ciphertext);
}
