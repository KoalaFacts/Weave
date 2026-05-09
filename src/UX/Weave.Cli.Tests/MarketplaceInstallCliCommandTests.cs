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
    public void SanitizeForEcho_LeavesPrintableUnchanged()
    {
        MarketplaceInstallCliCommand.SanitizeForEcho("plain text 123").ShouldBe("plain text 123");
        MarketplaceInstallCliCommand.SanitizeForEcho("a-b_c.d").ShouldBe("a-b_c.d");
    }

    [Fact]
    public void SanitizeForEcho_ReplacesAnsiEscapeSequences()
    {
        var input = "\u001b[31mred\u001b[0m";

        MarketplaceInstallCliCommand.SanitizeForEcho(input).ShouldBe("?[31mred?[0m");
    }

    [Fact]
    public void SanitizeForEcho_ReplacesNewlineAndTabAndBel()
    {
        var input = "line1\nline2\there\u0007done";

        MarketplaceInstallCliCommand.SanitizeForEcho(input).ShouldBe("line1?line2?here?done");
    }

    [Fact]
    public void SanitizeForEcho_ReplacesDelChar()
    {
        var input = "before\u007fafter";

        MarketplaceInstallCliCommand.SanitizeForEcho(input).ShouldBe("before?after");
    }

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
