using Weave.Security.Tokens;
using Weave.Shared.Capabilities;

namespace Weave.Security.Tests;

public sealed class ToolCapabilityTests
{
    [Theory]
    [InlineData("files", "read_file", "tool:files:invoke:read_file")]
    [InlineData("files:admin", "read:*", "tool:files%3Aadmin:invoke:read%3A%2A")]
    [InlineData("*", "*", "tool:%2A:invoke:%2A")]
    [InlineData("%2A", "a/b", "tool:%252A:invoke:a%2Fb")]
    public void Invoke_LiteralComponents_EscapesWithoutWildcardAuthority(string tool, string operation, string expected)
    {
        ToolCapability.Invoke(tool, operation).ShouldBe(expected);
        ToolCapability.Connect(tool).ShouldEndWith(":connect");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("tool:*")]
    [InlineData("tool:files:*")]
    [InlineData("tool:*:invoke:*")]
    [InlineData("tool:files:invoke:*")]
    public void ConstrainInvocations_BroadGrant_NarrowsToOnlyThisToolsInvocations(string grant)
    {
        var narrowed = ToolCapability.ConstrainInvocations("files", [grant]);
        narrowed.ShouldBe(["tool:files:invoke:*"]);
        CapabilityGrantMatcher.HasGrant(narrowed, ToolCapability.Connect("files")).ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(narrowed, ToolCapability.Invoke("other", "write_file")).ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(narrowed, "secret:*").ShouldBeFalse();
    }

    [Theory]
    [InlineData("tool:files")]
    [InlineData("tool:files:connect")]
    [InlineData("tool:other:invoke:*")]
    [InlineData("tool:files:invoke:")]
    [InlineData("tool:files:invoke:*:extra")]
    [InlineData("secret:*")]
    public void ConstrainInvocations_UnrelatedOrIncompleteGrant_ReturnsNoAuthority(string grant)
    {
        ToolCapability.ConstrainInvocations("files", [grant]).ShouldBeEmpty();
    }

    [Fact]
    public void ConstrainInvocations_ExactRead_DoesNotAcquireWriteOrLiteralWildcard()
    {
        var narrowed = ToolCapability.ConstrainInvocations("files", ["tool:*:invoke:read_file"]);
        narrowed.ShouldBe(["tool:files:invoke:read_file"]);
        CapabilityGrantMatcher.HasGrant(narrowed, ToolCapability.Invoke("files", "write_file")).ShouldBeFalse();
        var literal = ToolCapability.ConstrainInvocations("files", [ToolCapability.Invoke("files", "*")]);
        CapabilityGrantMatcher.HasGrant(literal, ToolCapability.Invoke("files", "write_file")).ShouldBeFalse();
    }

    [Fact]
    public void ConstrainInvocations_MixedPatterns_EveryReturnedPermissionWasAlreadyOwned()
    {
        string[] patterns = ["*", "tool:*", "*:files:invoke:read_file", "tool:*:invoke:*", "tool:files:connect",
            "tool:other:invoke:write_file", "tool:files:invoke:*:extra", "tool:files:invoke:%2A"];
        string[] requests = [ToolCapability.Connect("files"), "secret:token", ToolCapability.Invoke("other", "read_file"),
            ToolCapability.Invoke("files", "read_file"), ToolCapability.Invoke("files", "write_file"), ToolCapability.Invoke("files", "*")];
        foreach (var pattern in patterns)
        {
            var narrowed = ToolCapability.ConstrainInvocations("files", [pattern]);
            foreach (var request in requests)
                if (CapabilityGrantMatcher.HasGrant(narrowed, request))
                    CapabilityGrantMatcher.HasGrant([pattern], request).ShouldBeTrue($"{pattern} cannot gain {request}");
        }
    }
}
