namespace Weave.Invocations;

public sealed record FileWriteApprovalPlan(
    InvocationId InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string Root,
    string FileName,
    string Content,
    long MaxWriteBytes,
    string Revision);
