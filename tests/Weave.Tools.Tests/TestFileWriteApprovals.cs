using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Tools.Tests;

internal static class TestFileWriteApprovals
{
    // Explicitly unconfigured unit fixture. Real approval policy/storage is tested by the Host suite.
    public static FileWriteApprovalService Create() => new(
        Substitute.For<IInvocationApprovalStore>(), Substitute.For<IApprovalPlanProtector>(),
        Options.Create(new InvocationApprovalOptions()), Substitute.For<ICapabilityAuthorizer>(),
        Substitute.For<ICapabilityTokenService>(), TimeProvider.System);
}
