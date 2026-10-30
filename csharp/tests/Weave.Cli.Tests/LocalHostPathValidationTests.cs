using System.Text.Json;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalHostPathValidationTests
{
    [Theory]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("ancestor", false)]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    [InlineData("ancestor", true)]
    public void Prepare_RedirectedHost_RejectsBeforeCreatingPrivateConfiguration(string alias, bool dll)
    {
        using var files = new LocalTestDirectory();
        var host = CreateRedirectedHost(files, alias, dll);
        var result = new LocalDeploymentStore().Prepare(files.Private, files.Documents, host, "onboarding", 9401);
        result.Deployment.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("symbolic links");
        Directory.Exists(files.Private).ShouldBeFalse();
        Directory.GetFileSystemEntries(files.Documents).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("ancestor", false)]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    [InlineData("ancestor", true)]
    public async Task RunAsync_RedirectedHost_RejectsBeforePortChecksOrCredentialReads(string alias, bool initialize)
    {
        using var files = new LocalTestDirectory();
        var host = CreateRedirectedHost(files, alias, dll: initialize);
        var store = new LocalCredentialBoundaryStore();
        var runner = new LocalHostRunner(store, TimeProvider.System);
        // A reserved port keeps the unfixed implementation from starting any fixture process.
        var deployment = new LocalDeployment(host, files.Documents, "onboarding", 11111);
        var failure = await Should.ThrowAsync<ArgumentException>(() => runner.RunAsync(files.Private,
            deployment, initialize, TestContext.Current.CancellationToken));
        failure.Message.ShouldContain("symbolic links");
        store.OperatorReads.ShouldBe(0);
        store.SigningReads.ShouldBe(0);
        Directory.Exists(files.Private).ShouldBeFalse();
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("ancestor")]
    public void Load_RetainedHostRedirected_RejectsBeforeReadingPrivateConfiguration(string alias)
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        var deployment = store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401).Deployment!;
        File.WriteAllText(Path.Join(files.Private, "state", "invocations.db"), "existence fixture only");
        Directory.CreateDirectory(Path.Join(files.Private, "state", "revocations"));
        store.CompleteInitialization(files.Private, deployment).ShouldBeTrue();
        var redirected = deployment with { HostPath = CreateRedirectedHost(files, alias, dll: true) };
        var marker = Path.Join(files.Private, "local.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(redirected, LocalJsonContext.Default.LocalDeployment));
        var retained = File.ReadAllBytes(marker);
        var configuration = LocalDeploymentStore.HostConfigurationPath(files.Private);
        File.Move(configuration, configuration + ".unread");
        LocalSetupResult? result = null;
        var failure = Record.Exception(() => result = store.Load(files.Private));
        failure.ShouldBeNull("unsafe Host paths must be rejected before opening private configuration");
        result.ShouldNotBeNull();
        result.Deployment.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("symbolic links");
        File.ReadAllBytes(marker).ShouldBe(retained);
        File.Exists(configuration).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_OrdinaryPublishedHost_PreservesSupportedExecutableAndDllPaths(bool dll)
    {
        using var files = new LocalTestDirectory();
        var host = CreateHost(files, dll);
        var result = new LocalDeploymentStore().Prepare(files.Private, files.Documents, host, "onboarding", 9401);
        result.Error.ShouldBeNull();
        result.Deployment.ShouldNotBeNull();
        result.Deployment.HostPath.ShouldBe(host);
        File.Exists(LocalDeploymentStore.HostConfigurationPath(files.Private)).ShouldBeTrue();
    }

    [Fact]
    public void Load_HostNoLongerPresent_PreservesStatusAndReviewConfiguration()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        var deployment = store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401).Deployment!;
        File.WriteAllText(Path.Join(files.Private, "state", "invocations.db"), "existence fixture only");
        Directory.CreateDirectory(Path.Join(files.Private, "state", "revocations"));
        store.CompleteInitialization(files.Private, deployment).ShouldBeTrue();
        File.Delete(files.Host);
        store.Load(files.Private).Deployment.ShouldBe(deployment);
    }

    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\??\")]
    public void Prepare_WindowsDeviceHostAlias_RejectsBeforeCreatingPrivateConfiguration(string prefix)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This case exercises real Windows device-path aliases.");
        using var files = new LocalTestDirectory();
        var host = CreateHost(files, dll: false);
        var result = new LocalDeploymentStore().Prepare(files.Private, files.Documents, prefix + host, "onboarding", 9401);
        result.Deployment.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Windows device and extended path namespaces");
        Directory.Exists(files.Private).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"\\?\", false)]
    [InlineData(@"\\?\", true)]
    [InlineData(@"\??\", false)]
    [InlineData(@"\??\", true)]
    public async Task RunAsync_WindowsDeviceHostAlias_RejectsBeforePortChecks(string prefix, bool initialize)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This case exercises real Windows device-path aliases.");
        using var files = new LocalTestDirectory();
        var host = CreateHost(files, dll: initialize);
        var store = new LocalCredentialBoundaryStore();
        var runner = new LocalHostRunner(store, TimeProvider.System);
        var deployment = new LocalDeployment(prefix + host, files.Documents, "onboarding", 11111);
        var failure = await Should.ThrowAsync<ArgumentException>(() => runner.RunAsync(files.Private,
            deployment, initialize, TestContext.Current.CancellationToken));
        failure.Message.ShouldContain("Windows device and extended path namespaces");
        store.OperatorReads.ShouldBe(0);
        store.SigningReads.ShouldBe(0);
    }

    private static string CreateHost(LocalTestDirectory files, bool dll)
    {
        var bundle = Path.Join(files.Root, "distribution", "host");
        Directory.CreateDirectory(bundle);
        var name = dll ? "Weave.Silo.dll" : OperatingSystem.IsWindows() ? "Weave.Silo.exe" : "Weave.Silo";
        var host = Path.Join(bundle, name);
        File.WriteAllText(host, "Host fixture; never executed");
        return host;
    }

    private static string CreateRedirectedHost(LocalTestDirectory files, string alias, bool dll)
    {
        var host = CreateHost(files, dll);
        var bundle = Path.GetDirectoryName(host)!;
        var link = Path.Join(files.Root, "alias");
        try
        {
            if (alias == "file")
            {
                Directory.CreateDirectory(link);
                var linkedHost = Path.Join(link, Path.GetFileName(host));
                File.CreateSymbolicLink(linkedHost, host);
                return linkedHost;
            }
            Directory.CreateSymbolicLink(link, alias == "ancestor" ? Path.GetDirectoryName(bundle)! : bundle);
            return alias == "ancestor" ? Path.Join(link, "host", Path.GetFileName(host)) : Path.Join(link, Path.GetFileName(host));
        }
        catch (Exception failure) when (OperatingSystem.IsWindows() && failure is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This Windows environment cannot create the symbolic link fixture.");
            throw;
        }
    }
}
