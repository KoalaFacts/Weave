using Weave.Tools.Tool;

namespace Weave.Invocations;

public sealed record InvocationProposalReadResult(ToolInvocation? Request, string? ErrorCode);
