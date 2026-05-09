using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public class MarketplaceInstallCliCommandTests
{
    private static readonly Lazy<Command> InstallCommand = new(BuildInstallCommand);

    [Theory]
    [InlineData("starter")]
    [InlineData("my-workspace")]
    [InlineData("workspace_with_underscore")]
    [InlineData("workspace.with.dots")]
    [InlineData("a")]
    [InlineData("...trailing-dots-ok")]
    [InlineData("ws with space")]
    public void IsSafeWorkspaceName_PlainNames_AreAccepted(string name) =>
        MarketplaceInstallCliCommand.IsSafeWorkspaceName(name).ShouldBeTrue();

    [Theory]
    [InlineData("../etc")]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("/abs/path")]
    [InlineData("\\windows\\path")]
    [InlineData("../foo")]
    [InlineData("foo/..")]
    [InlineData("foo/../bar")]
    [InlineData("C:foo")]
    [InlineData("C:")]
    [InlineData("D:..\\evil")]
    [InlineData("foo:bar")]
    public void IsSafeWorkspaceName_PathTraversalShapes_AreRejected(string name) =>
        MarketplaceInstallCliCommand.IsSafeWorkspaceName(name).ShouldBeFalse();

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("con")]
    [InlineData("Nul")]
    [InlineData("CON.txt")]
    [InlineData("nul.tar.gz")]
    public void IsSafeWorkspaceName_WindowsReservedNames_AreRejected(string name) =>
        MarketplaceInstallCliCommand.IsSafeWorkspaceName(name).ShouldBeFalse();

    [Fact]
    public void InstallCommand_ExposesNoScaffoldFlag() =>
        InstallCommand.Value.Options.ShouldContain(o => o.Name == "--no-scaffold");

    [Fact]
    public void InstallCommand_ExposesWorkspaceNameOption() =>
        InstallCommand.Value.Options.ShouldContain(o => o.Name == "--workspace-name");

    [Fact]
    public void InstallCommand_ItemIdArgumentIsOptional()
    {
        var itemIdArg = InstallCommand.Value.Arguments.ShouldHaveSingleItem();
        itemIdArg.Name.ShouldBe("item-id");
        itemIdArg.Arity.MinimumNumberOfValues.ShouldBe(0);
    }

    [Fact]
    public void InstallCommand_ParsesNoScaffoldFlag()
    {
        var result = InstallCommand.Value.Parse("--no-scaffold item-1");

        result.GetValue<bool>("--no-scaffold").ShouldBeTrue();
    }

    [Fact]
    public void InstallCommand_ParsesWorkspaceNameOption()
    {
        var result = InstallCommand.Value.Parse("--workspace-name custom-name item-1");

        result.GetValue<string?>("--workspace-name").ShouldBe("custom-name");
    }

    [Fact]
    public void InstallCommand_DefaultsNoScaffoldToFalseAndWorkspaceNameToNull()
    {
        var result = InstallCommand.Value.Parse("item-1");

        result.GetValue<bool>("--no-scaffold").ShouldBeFalse();
        result.GetValue<string?>("--workspace-name").ShouldBeNull();
    }

    private static Command BuildInstallCommand()
    {
        var services = CliServiceCollection.Build();
        var marketplace = MarketplaceCommands.Create(
            services.GetRequiredService<MarketplaceListCliCommand>(),
            services.GetRequiredService<MarketplaceSearchCliCommand>(),
            services.GetRequiredService<MarketplaceSubmitCliCommand>(),
            services.GetRequiredService<MarketplacePublishCliCommand>(),
            services.GetRequiredService<MarketplaceInfoCliCommand>(),
            services.GetRequiredService<MarketplaceInstallCliCommand>());

        return marketplace.Subcommands.Single(c => c.Name == "install");
    }
}
