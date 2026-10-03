using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalCodexLauncherTests
{
    [Fact]
    public void BuildStartInfo_LocalSession_DisablesInstalledPluginsWithoutBypassingSandbox()
    {
        using var files = new LocalTestDirectory();
        var deployment = new LocalDeployment(files.Host, files.Documents, "onboarding", 9401);
        var start = LocalCodexLauncher.BuildStartInfo("codex", files.Private, deployment,
            Path.Join(files.Root, "agent"), "Query the retained UUID.", execute: true);
        var arguments = start.ArgumentList.ToArray();
        var disable = Array.IndexOf(arguments, "--disable");
        disable.ShouldBeGreaterThanOrEqualTo(0);
        arguments[disable + 1].ShouldBe("plugins");
        arguments.ShouldContain("--ignore-user-config");
        arguments.ShouldContain("--approve-for-me");
        arguments.Any(argument => argument.StartsWith("--dangerously", StringComparison.Ordinal)).ShouldBeFalse();
        arguments.ShouldContain("mcp_servers.weave_files.enabled_tools=[\"read_document\",\"submit_write\",\"get_status\",\"resume_write\"]");
    }
}
