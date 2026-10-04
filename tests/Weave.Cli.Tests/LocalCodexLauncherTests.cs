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

    [Theory]
    [InlineData("same", false)]
    [InlineData("ancestor", true)]
    [InlineData("descendant", false)]
    public async Task RunAsync_HostBundleOverlap_RejectsBeforeCredentialsOrWorkspaceChanges(string relation, bool execute)
    {
        using var files = new LocalTestDirectory();
        var bundle = Path.Join(files.Root, "distribution", "host");
        Directory.CreateDirectory(bundle);
        var host = Path.Join(bundle, "Weave.Silo");
        File.WriteAllText(host, "Host sentinel; never executed");
        var agent = relation switch
        {
            "same" => bundle,
            "ancestor" => Path.GetDirectoryName(bundle)!,
            _ => Path.Join(bundle, "agent")
        };
        await AssertRejectedAsync(files, host, agent, execute);
        File.ReadAllText(host).ShouldBe("Host sentinel; never executed");
        Directory.Exists(Path.Join(bundle, "agent")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("host-directory")]
    [InlineData("host-ancestor")]
    [InlineData("host-file")]
    [InlineData("agent-directory")]
    [InlineData("agent-ancestor")]
    public async Task RunAsync_RedirectedPaths_RejectsBeforeCredentials(string alias)
    {
        using var files = new LocalTestDirectory();
        var bundle = Path.Join(files.Root, "distribution", "host");
        Directory.CreateDirectory(bundle);
        var host = Path.Join(bundle, "Weave.Silo");
        File.WriteAllText(host, "Host sentinel; never executed");
        var agent = Path.Join(files.Root, "agent");
        var link = Path.Join(files.Root, "alias");
        try
        {
            if (alias == "host-file")
            {
                Directory.CreateDirectory(link);
                File.CreateSymbolicLink(Path.Join(link, "Weave.Silo"), host);
                host = Path.Join(link, "Weave.Silo");
                agent = bundle;
            }
            else
            {
                Directory.CreateSymbolicLink(link, alias == "host-ancestor" ? Path.GetDirectoryName(bundle)! : bundle);
                if (alias.StartsWith("host", StringComparison.Ordinal))
                {
                    host = alias == "host-ancestor" ? Path.Join(link, "host", "Weave.Silo") : Path.Join(link, "Weave.Silo");
                    agent = bundle;
                }
                else
                    agent = alias == "agent-ancestor" ? Path.Join(link, "agent") : link;
            }
        }
        catch (Exception failure) when (OperatingSystem.IsWindows() && failure is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This Windows environment cannot create the symbolic link fixture.");
        }
        await AssertRejectedAsync(files, host, agent, execute: true);
        File.ReadAllText(Path.Join(bundle, "Weave.Silo")).ShouldBe("Host sentinel; never executed");
        Directory.Exists(Path.Join(bundle, "agent")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("host-extra")]
    public async Task RunAsync_SeparateHostSibling_ReachesCredentialBoundary(string name)
    {
        using var files = new LocalTestDirectory();
        var bundle = Path.Join(files.Root, "host");
        Directory.CreateDirectory(bundle);
        var host = Path.Join(bundle, "Weave.Silo");
        File.WriteAllText(host, "Host sentinel; never executed");
        var agent = Path.Join(files.Root, name);
        var store = new LocalCredentialBoundaryStore();
        var launcher = new LocalCodexLauncher(store, TimeProvider.System);
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => launcher.RunAsync(files.Private,
            new LocalDeployment(host, files.Documents, "onboarding", 9401), "never-executed", agent, null, false,
            TestContext.Current.CancellationToken));
        failure.Message.ShouldBe("credential boundary sentinel");
        store.OperatorReads.ShouldBe(1);
        Directory.Exists(agent).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_SeparatelyPublishedCliWorkspace_RejectsBeforeCredentials()
    {
        using var files = new LocalTestDirectory();
        await AssertRejectedAsync(files, files.Host, AppContext.BaseDirectory, execute: true);
    }

    [Fact]
    public async Task RunAsync_RuntimeExecutableWorkspace_RejectsBeforeCredentials()
    {
        using var files = new LocalTestDirectory();
        var executable = Environment.ProcessPath;
        executable.ShouldNotBeNull();
        var directory = Path.GetDirectoryName(executable);
        directory.ShouldNotBeNull();
        await AssertRejectedAsync(files, files.Host, directory, execute: false);
    }

    [Theory]
    [InlineData(@"\\?\", false)]
    [InlineData(@"\\?\", true)]
    [InlineData(@"\??\", false)]
    [InlineData(@"\??\", true)]
    public async Task RunAsync_WindowsDeviceAlias_RejectsBeforeCredentials(string prefix, bool hostAlias)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This case exercises real Windows device-path aliases.");
        using var files = new LocalTestDirectory();
        var bundle = Path.Join(files.Root, "host");
        Directory.CreateDirectory(bundle);
        var host = Path.Join(bundle, "Weave.Silo");
        File.WriteAllText(host, "Host sentinel; never executed");
        var alias = prefix + bundle;
        await AssertRejectedAsync(files, hostAlias ? Path.Join(alias, "Weave.Silo") : host,
            hostAlias ? bundle : alias, execute: true);
        File.ReadAllText(host).ShouldBe("Host sentinel; never executed");
    }

    private static async Task AssertRejectedAsync(LocalTestDirectory files, string host, string agent, bool execute)
    {
        var store = new LocalCredentialBoundaryStore();
        var launcher = new LocalCodexLauncher(store, TimeProvider.System);
        int? exit = null;
        var failure = await Record.ExceptionAsync(async () => exit = await launcher.RunAsync(files.Private,
            new LocalDeployment(host, files.Documents, "onboarding", 9401), "never-executed", agent,
            execute ? "Continue the original UUID." : null, execute, TestContext.Current.CancellationToken));
        failure.ShouldBeNull("unsafe paths must be rejected before reading credentials or making HTTP requests");
        exit.ShouldBe(1);
        store.OperatorReads.ShouldBe(0);
        store.SigningReads.ShouldBe(0);
        Directory.Exists(files.Private).ShouldBeFalse();
    }

}
