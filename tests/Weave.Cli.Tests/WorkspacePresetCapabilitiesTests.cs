using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;
namespace Weave.Cli.Tests;

public class WorkspacePresetCapabilitiesTests
{
    [Fact]
    public void CodingAssistantPreset_DeclaresToolGrants()
    {
        WorkspacePresets.All["coding-assistant"].Capabilities
            .ShouldBe(["tool:git:invoke:exec", "tool:files:invoke:*"]);
    }

    [Fact]
    public void ResearchPreset_DeclaresToolGrants()
    {
        WorkspacePresets.All["research"].Capabilities
            .ShouldBe(["tool:web-search:invoke:*", "tool:files:invoke:*"]);
    }

    [Fact]
    public void MultiAgentPreset_DeclaresToolGrants()
    {
        WorkspacePresets.All["multi-agent"].Capabilities
            .ShouldBe(["tool:git:invoke:exec", "tool:files:invoke:*", "tool:web-search:invoke:*"]);
    }

    [Fact]
    public void SupportTeamPreset_DeclaresToolChannelSkillAndUserGrants()
    {
        var capabilities = WorkspacePresets.All["support-team"].Capabilities;

        capabilities.ShouldContain("tool:web-search:invoke:*");
        capabilities.ShouldContain("tool:files:invoke:*");
        capabilities.ShouldContain("channel:send:slack");
        capabilities.ShouldContain("channel:receive:slack");
        capabilities.ShouldContain("skill:read");
        capabilities.ShouldContain("skill:write");
        capabilities.ShouldContain("user:read:*");
        capabilities.ShouldContain("user:write:*");
    }

    [Fact]
    public void StarterPreset_DeclaresEmptyCapabilities()
    {
        WorkspacePresets.All["starter"].Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public void EveryPresetCoversItsToolList()
    {
        // The contract: every tool a preset hands to an agent must be covered
        // by a capability grant in that preset, otherwise the runtime will
        // deny invocation. Asserted by walking the matcher rule directly so a
        // future preset addition can't drop coherence by accident.
        foreach (var (name, preset) in WorkspacePresets.All)
        {
            foreach (var tool in preset.Tools)
            {
                Weave.Security.Tokens.ToolCapability.ConstrainInvocations(tool, preset.Capabilities)
                    .ShouldNotBeEmpty($"preset '{name}' declares tool '{tool}' but no invocation grant applies");
            }
        }
    }

    [Fact]
    public void CodingAssistantPreset_FactoryEmitsCapabilitiesOnAgent()
    {
        var preset = WorkspacePresets.All["coding-assistant"];
        var selection = new WorkspaceNewSelection(
            preset.Model,
            [.. preset.Tools],
            "coding-assistant",
            IsolationLevel.Full,
            preset.Capabilities);

        var template = WorkspaceNewTemplateFactory.Create(selection);

        template.Agents.ShouldContainKey("assistant");
        template.Agents["assistant"].Capabilities.ShouldBe(["tool:git:invoke:exec", "tool:files:invoke:*"]);
    }

    [Fact]
    public void SupportTeamPreset_FactoryEmitsBotCapabilitiesAndMonitorBaseline()
    {
        var preset = WorkspacePresets.All["support-team"];
        var selection = new WorkspaceNewSelection(
            preset.Model,
            [.. preset.Tools],
            "support-team",
            IsolationLevel.Full,
            preset.Capabilities);

        var template = WorkspaceNewTemplateFactory.Create(selection);

        template.Agents.ShouldContainKey("support-bot");
        template.Agents["support-bot"].Capabilities.ShouldContain("channel:send:slack");
        template.Agents["support-bot"].Capabilities.ShouldContain("skill:write");

        // Monitor's hard-coded tool list (web-search only) gets a matching
        // hard-coded grant so the emitted manifest is internally coherent.
        template.Agents.ShouldContainKey("monitor");
        template.Agents["monitor"].Capabilities.ShouldBe(["tool:web-search:invoke:*"]);
    }

    [Fact]
    public void EveryPreset_PrimaryTemplate_IsTheBuiltInTemplate()
    {
        // Sanity check that each preset is wired to the matching BuiltInTemplate
        // by reference — not a duplicate. Reference equality is the property
        // the previous drift-detector theory was approximating with structural
        // assertions, now collapsed since the two surfaces share one object.
        WorkspacePresets.All["starter"].PrimaryTemplate.ShouldBeSameAs(BuiltInTemplates.Starter);
        WorkspacePresets.All["coding-assistant"].PrimaryTemplate.ShouldBeSameAs(BuiltInTemplates.CodingAssistant);
        WorkspacePresets.All["research"].PrimaryTemplate.ShouldBeSameAs(BuiltInTemplates.Research);
        WorkspacePresets.All["multi-agent"].PrimaryTemplate.ShouldBeSameAs(BuiltInTemplates.MultiAgentSupervisor);
        WorkspacePresets.All["support-team"].PrimaryTemplate.ShouldBeSameAs(BuiltInTemplates.SupportBot);
    }
}
