using System.Diagnostics;
using System.Globalization;

namespace Weave.Cli.Tests;

public sealed class SiloLauncherProcessGroupTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadProcessFields_ProcessExitsAfterStatIsOpened_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This regression exercises Linux procfs process exit semantics.");
        using var child = Process.Start(new ProcessStartInfo("/bin/cat")
        {
            UseShellExecute = false,
            RedirectStandardInput = true
        });
        child.ShouldNotBeNull();
        try
        {
            var path = $"/proc/{child.Id.ToString(CultureInfo.InvariantCulture)}/stat";
            using var reader = new StreamReader(path);
            child.StandardInput.Close();
            await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var fields = SiloLauncherProcessGroup.ReadProcessFields(child.Id, _ => reader.ReadToEnd());

            fields.ShouldBeNull();
        }
        finally
        {
            await StopChildAsync(child);
        }
    }

    [Fact]
    public void ReadProcessFields_LiveProcess_ReturnsStateAndGroup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This case reads Linux procfs process metadata.");

        var fields = SiloLauncherProcessGroup.ReadProcessFields(Environment.ProcessId, File.ReadAllText);

        fields.ShouldNotBeNull();
        fields[0].ShouldBeOneOf("R", "S");
        int.Parse(fields[2], CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(unchecked((int)0x80070003))]
    public void ReadProcessFields_OtherIoFailure_PropagatesFailure(int errorCode)
    {
        var failure = new IOException("The stat read failed for a reason other than process exit.", errorCode);

        var thrown = Should.Throw<IOException>(() =>
            SiloLauncherProcessGroup.ReadProcessFields(Environment.ProcessId, _ => throw failure));

        thrown.ShouldBeSameAs(failure);
    }

    [Fact]
    public void ReadProcessFields_AccessDenied_PropagatesFailure()
    {
        var failure = new UnauthorizedAccessException("The stat read is denied.");

        var thrown = Should.Throw<UnauthorizedAccessException>(() =>
            SiloLauncherProcessGroup.ReadProcessFields(Environment.ProcessId, _ => throw failure));

        thrown.ShouldBeSameAs(failure);
    }

    private static async Task StopChildAsync(Process child)
    {
        child.StandardInput.Close();
        if (!child.HasExited)
            child.Kill();
        await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
