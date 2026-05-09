using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public class MarketplaceInstallCliCommandTests
{
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
    public void IsSafeWorkspaceName_PathTraversalShapes_AreRejected(string name) =>
        MarketplaceInstallCliCommand.IsSafeWorkspaceName(name).ShouldBeFalse();

    [Fact]
    public void InstallCommand_ExposesNoScaffoldFlag()
    {
        var install = ResolveInstallCommand();

        install.Options.ShouldContain(o => o.Name == "--no-scaffold");
    }

    [Fact]
    public void InstallCommand_ExposesWorkspaceNameOption()
    {
        var install = ResolveInstallCommand();

        install.Options.ShouldContain(o => o.Name == "--workspace-name");
    }

    [Fact]
    public void InstallCommand_ItemIdArgumentIsOptional()
    {
        var install = ResolveInstallCommand();

        var itemIdArg = install.Arguments.ShouldHaveSingleItem();
        itemIdArg.Name.ShouldBe("item-id");
        itemIdArg.Arity.MinimumNumberOfValues.ShouldBe(0);
    }

    [Fact]
    public void InstallCommand_ParsesNoScaffoldFlag()
    {
        var install = ResolveInstallCommand();

        var result = install.Parse("--no-scaffold item-1");

        var noScaffoldOption = install.Options.Single(o => o.Name == "--no-scaffold");
        result.GetValue<bool>(noScaffoldOption.Name).ShouldBeTrue();
    }

    [Fact]
    public void InstallCommand_ParsesWorkspaceNameOption()
    {
        var install = ResolveInstallCommand();

        var result = install.Parse("--workspace-name custom-name item-1");

        var workspaceNameOption = install.Options.Single(o => o.Name == "--workspace-name");
        result.GetValue<string?>(workspaceNameOption.Name).ShouldBe("custom-name");
    }

    [Fact]
    public void InstallCommand_DefaultsNoScaffoldToFalseAndWorkspaceNameToNull()
    {
        var install = ResolveInstallCommand();

        var result = install.Parse("item-1");

        var noScaffoldOption = install.Options.Single(o => o.Name == "--no-scaffold");
        var workspaceNameOption = install.Options.Single(o => o.Name == "--workspace-name");
        result.GetValue<bool>(noScaffoldOption.Name).ShouldBeFalse();
        result.GetValue<string?>(workspaceNameOption.Name).ShouldBeNull();
    }

    private static System.CommandLine.Command ResolveInstallCommand()
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
