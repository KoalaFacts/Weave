using Weave.Tools.InstallMcpTool;

namespace Weave.Workspaces.Tests;

public sealed class McpToolInstallationTests
{
    [Fact]
    public void ComputeConfigDigest_DelimiterInFields_DistinguishesRevisions()
    {
        var first = McpToolInstallation.ComputeConfigDigest("http://127.0.0.1:9000/mcp", "server\nname", "1", "echo");
        var second = McpToolInstallation.ComputeConfigDigest("http://127.0.0.1:9000/mcp", "server", "name\n1", "echo");

        first.ShouldNotBe(second);
        first.ShouldBe(McpToolInstallation.ComputeConfigDigest("http://127.0.0.1:9000/mcp", "server\nname", "1", "echo"));
    }
}
