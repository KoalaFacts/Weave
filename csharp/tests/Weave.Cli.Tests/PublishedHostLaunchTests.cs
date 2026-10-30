using Shouldly;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class PublishedHostLaunchTests
{
    [Theory]
    [InlineData("Weave.Silo.exe")]
    [InlineData("Weave.Silo")]
    public void BuildSiloArgs_PublishedExecutable_LaunchesWithoutDotnet(string executable)
    {
        var result = SiloProcessService.BuildSiloArgs(executable, 9401);
        result.FileName.ShouldBe(executable);
        result.Arguments.ShouldNotContain(executable);
        result.Arguments.ShouldContain("--Weave:LocalMode=true");
    }

    [Fact]
    public void BuildSiloArgs_FrameworkDependentDll_UsesDotnet()
    {
        var result = SiloProcessService.BuildSiloArgs("Weave.Silo.dll", 9401);
        result.FileName.ShouldBe("dotnet");
        result.Arguments[0].ShouldBe("Weave.Silo.dll");
    }
}
