using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class InvocationApprovalPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tool:files")]
    [InlineData("tool:files:connect")]
    [InlineData("tool::invoke:write_file")]
    [InlineData("tool:files:invoke:write_file ")]
    public void Construct_InvalidRequirement_RejectsConfiguration(string? grant)
    {
        var options = new InvocationJournalOptions { ApprovalRequiredGrants = [grant!] };
        Should.Throw<ArgumentException>(() => new InvocationApprovalPolicy(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(604801)]
    public void Construct_InvalidLifetime_RejectsConfiguration(int seconds)
    {
        var options = new InvocationJournalOptions { ApprovalLifetime = TimeSpan.FromSeconds(seconds) };
        Should.Throw<ArgumentException>(() => new InvocationApprovalPolicy(options));
    }

    [Fact]
    public void RequiresApproval_CallerMutatesConfiguration_PreservesSnapshot()
    {
        var options = new InvocationJournalOptions
        {
            ApprovalRequiredGrants = ["tool:files:invoke:write_file"],
            ApprovalLifetime = TimeSpan.FromMinutes(5)
        };
        var policy = new InvocationApprovalPolicy(options);
        options.ApprovalRequiredGrants.Clear();
        options.ApprovalLifetime = TimeSpan.FromDays(1);
        policy.RequiresApproval("tool:files:invoke:write_file").ShouldBeTrue();
        policy.RequiresApproval("tool:files:invoke:read_file").ShouldBeFalse();
        policy.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Approve_LiteralComponents_DoesNotCreateWildcardAuthority()
    {
        ToolCapability.Approve("files:admin", "read:*").ShouldBe("tool:files%3Aadmin:approve:read%3A%2A");
        ToolCapability.ConstrainInvocations("files", ["tool:files:approve:write_file"]).ShouldBeEmpty();
    }
}
