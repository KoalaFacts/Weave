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

    [Theory]
    [InlineData("tool:files:invoke:write_file", 0)]
    [InlineData("tool:files:invoke:write_file", 4)]
    [InlineData("tool:files:invoke:read_file", 2)]
    [InlineData("tool:files:invoke:*", 2)]
    public void Construct_InvalidQuorum_RejectsConfiguration(string grant, int approvals)
    {
        var options = new InvocationJournalOptions
        {
            ApprovalRequiredGrants = ["tool:files:invoke:write_file"],
            ApprovalQuorums = [new InvocationApprovalQuorum { Grant = grant, RequiredApprovals = approvals }]
        };
        Should.Throw<ArgumentException>(() => new InvocationApprovalPolicy(options));
    }

    [Fact]
    public void Construct_DuplicateQuorum_RejectsConfiguration()
    {
        var options = new InvocationJournalOptions
        {
            ApprovalRequiredGrants = ["tool:files:invoke:write_file"],
            ApprovalQuorums =
            [
                new InvocationApprovalQuorum { Grant = "tool:files:invoke:write_file", RequiredApprovals = 2 },
                new InvocationApprovalQuorum { Grant = "tool:files:invoke:write_file", RequiredApprovals = 3 }
            ]
        };
        Should.Throw<ArgumentException>(() => new InvocationApprovalPolicy(options));
    }

    [Fact]
    public void RequiredApprovals_ConfiguredQuorum_SnapshotsPerOperation()
    {
        var options = new InvocationJournalOptions
        {
            ApprovalRequiredGrants = ["tool:files:invoke:write_file", "tool:mail:invoke:send"],
            ApprovalQuorums = [new InvocationApprovalQuorum { Grant = "tool:mail:invoke:send", RequiredApprovals = 2 }]
        };
        var policy = new InvocationApprovalPolicy(options);
        options.ApprovalQuorums[0].RequiredApprovals = 3;
        options.ApprovalQuorums.Clear();
        policy.RequiredApprovals("tool:mail:invoke:send").ShouldBe(2);
        policy.RequiredApprovals("tool:files:invoke:write_file").ShouldBe(1);
    }

    [Fact]
    public void Approve_LiteralComponents_DoesNotCreateWildcardAuthority()
    {
        ToolCapability.Approve("files:admin", "read:*").ShouldBe("tool:files%3Aadmin:approve:read%3A%2A");
        ToolCapability.ConstrainInvocations("files", ["tool:files:approve:write_file"]).ShouldBeEmpty();
    }
}
