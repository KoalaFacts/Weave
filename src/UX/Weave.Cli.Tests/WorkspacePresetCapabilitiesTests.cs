using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public class WorkspacePresetCapabilitiesTests
{
    [Fact]
    public void CodingAssistantPreset_DeclaresToolGrants()
    {
        var preset = WorkspacePresets.All["coding-assistant"];

        preset.Capabilities.ShouldNotBeNull();
        preset.Capabilities.ShouldBe(["tool:git", "tool:files"]);
    }

    [Fact]
    public void ResearchPreset_DeclaresToolGrants()
    {
        var preset = WorkspacePresets.All["research"];

        preset.Capabilities.ShouldNotBeNull();
        preset.Capabilities.ShouldBe(["tool:web-search", "tool:files"]);
    }

    [Fact]
    public void MultiAgentPreset_DeclaresToolGrants()
    {
        var preset = WorkspacePresets.All["multi-agent"];

        preset.Capabilities.ShouldNotBeNull();
        preset.Capabilities.ShouldBe(["tool:git", "tool:files", "tool:web-search"]);
    }

    [Fact]
    public void SupportTeamPreset_DeclaresToolChannelSkillAndUserGrants()
    {
        var preset = WorkspacePresets.All["support-team"];

        preset.Capabilities.ShouldNotBeNull();
        preset.Capabilities.ShouldContain("tool:web-search");
        preset.Capabilities.ShouldContain("tool:files");
        preset.Capabilities.ShouldContain("channel:send:slack");
        preset.Capabilities.ShouldContain("channel:receive:slack");
        preset.Capabilities.ShouldContain("skill:read");
        preset.Capabilities.ShouldContain("skill:write");
        preset.Capabilities.ShouldContain("user:read:*");
        preset.Capabilities.ShouldContain("user:write:*");
    }

    [Fact]
    public void StarterPreset_DeclaresEmptyCapabilities()
    {
        var preset = WorkspacePresets.All["starter"];

        preset.Capabilities.ShouldNotBeNull();
        preset.Capabilities.ShouldBeEmpty();
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
            var owned = preset.Capabilities ?? [];
            foreach (var tool in preset.Tools)
            {
                Weave.Shared.Capabilities.CapabilityGrantMatcher
                    .HasGrant(owned, $"tool:{tool}")
                    .ShouldBeTrue($"preset '{name}' declares tool '{tool}' but no capability grant covers 'tool:{tool}'");
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
            Weave.Workspaces.Models.IsolationLevel.Full,
            preset.Capabilities ?? []);

        var template = WorkspaceNewTemplateFactory.Create(selection);

        template.Agents.ShouldContainKey("assistant");
        template.Agents["assistant"].Capabilities.ShouldBe(["tool:git", "tool:files"]);
    }

    [Fact]
    public void SupportTeamPreset_FactoryEmitsBotCapabilitiesAndMonitorBaseline()
    {
        var preset = WorkspacePresets.All["support-team"];
        var selection = new WorkspaceNewSelection(
            preset.Model,
            [.. preset.Tools],
            "support-team",
            Weave.Workspaces.Models.IsolationLevel.Full,
            preset.Capabilities ?? []);

        var template = WorkspaceNewTemplateFactory.Create(selection);

        template.Agents.ShouldContainKey("support-bot");
        template.Agents["support-bot"].Capabilities.ShouldContain("channel:send:slack");
        template.Agents["support-bot"].Capabilities.ShouldContain("skill:write");

        // Monitor's hard-coded tool list (web-search only) gets a matching
        // hard-coded grant so the emitted manifest is internally coherent.
        template.Agents.ShouldContainKey("monitor");
        template.Agents["monitor"].Capabilities.ShouldBe(["tool:web-search"]);
    }
}
