using Weave.Security.Tokens;

namespace Weave.Silo.Tests.Invocations;

/// <summary>Test fixtures mint approval grants only for human principals, as the token service requires.</summary>
internal static class TestPrincipals
{
    public static PrincipalKind For(IEnumerable<string> grants) =>
        grants.Any(PrincipalRequirement.IsApprovalGrant) ? PrincipalKind.Human : PrincipalKind.Agent;
}
