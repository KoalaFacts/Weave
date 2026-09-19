using Weave.Authority;
using Weave.Shared.Capabilities;

namespace Weave.Tools.Tests;

public sealed class ToolCapabilityTests
{
    [Fact]
    public void Invoke_OrdinaryNames_ProducesAnOperationGrant()
    {
        ToolCapability.Invoke("files", "read_file").ShouldBe("tool:files:invoke:read_file");
        ToolCapability.Connect("files").ShouldBe("tool:files:connect");
        ToolCapability.AllInvocations("files").ShouldBe("tool:files:invoke:*");
    }

    [Theory]
    [InlineData("files:other", "read:*", "tool:files%3Aother:invoke:read%3A%2A")]
    [InlineData("*", "*", "tool:%2A:invoke:%2A")]
    [InlineData("files/one", "%2A", "tool:files%2Fone:invoke:%252A")]
    public void Invoke_ReservedCharacters_EncodesSegmentsWithoutGrantingWildcards(string tool, string method, string expected)
    {
        var grant = ToolCapability.Invoke(tool, method);
        grant.ShouldBe(expected);
        CapabilityGrantMatcher.HasGrant([grant], ToolCapability.Invoke("other", "write")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("read\nwrite")]
    public void Invoke_InvalidOperation_Rejects(string method)
    {
        Should.Throw<ArgumentException>(() => ToolCapability.Invoke("files", method));
    }

    [Fact]
    public void Invoke_OversizedOrInvalidUnicode_Rejects()
    {
        Should.Throw<ArgumentException>(() => ToolCapability.Invoke("files", new string('x', 401)));
        Should.Throw<ArgumentException>(() => ToolCapability.Invoke("files", "\ud800"));
    }

    [Theory]
    [InlineData("*", "tool:files:invoke:*")]
    [InlineData("tool:*", "tool:files:invoke:*")]
    [InlineData("tool:files:*", "tool:files:invoke:*")]
    [InlineData("tool:*:invoke:read_file", "tool:files:invoke:read_file")]
    [InlineData("tool:files:*:read_file", "tool:files:invoke:read_file")]
    [InlineData("tool:files:invoke:*", "tool:files:invoke:*")]
    [InlineData("tool:files:invoke:read_file", "tool:files:invoke:read_file")]
    public void ConstrainInvocations_CoveringGrant_RestrictsToRequestedTool(string input, string expected)
    {
        var grants = ToolCapability.ConstrainInvocations([input], "files");
        grants.ShouldBe([expected]);
        CapabilityGrantMatcher.HasGrant(grants, ToolCapability.Connect("files")).ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(grants, ToolCapability.Invoke("other", "read_file")).ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(grants, "secret:all").ShouldBeFalse();
    }

    [Theory]
    [InlineData("tool:files")]
    [InlineData("tool:files:connect")]
    [InlineData("tool:files:invoke")]
    [InlineData("tool:files:invoke:")]
    [InlineData("tool:Files:invoke:read_file")]
    [InlineData("tool:other:invoke:read_file")]
    [InlineData("tool:files:invoke:read:*")]
    [InlineData("tool:files:invoke:%2a")]
    [InlineData("secret:*")]
    public void ConstrainInvocations_UnrelatedOrNoncanonicalGrant_ReturnsEmpty(string input)
    {
        ToolCapability.ConstrainInvocations([input], "files").ShouldBeEmpty();
    }

    [Fact]
    public void ConstrainInvocations_ReadGrant_DoesNotGrantWriteOrWildcard()
    {
        var grants = ToolCapability.ConstrainInvocations(["tool:*:invoke:read_file", "tool:files:invoke:read_file"], "files");
        grants.Count.ShouldBe(1);
        CapabilityGrantMatcher.HasGrant(grants, ToolCapability.Invoke("files", "read_file")).ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(grants, ToolCapability.Invoke("files", "write_file")).ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(grants, ToolCapability.Invoke("files", "*")).ShouldBeFalse();
    }
}
