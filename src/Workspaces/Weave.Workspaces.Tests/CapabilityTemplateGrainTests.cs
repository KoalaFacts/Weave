using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class CapabilityTemplateGrainTests
{
    private static IPersistentState<TemplateRegistryState> CreatePersistentState(TemplateRegistryState? state = null)
    {
        var persistentState = Substitute.For<IPersistentState<TemplateRegistryState>>();
        persistentState.State.Returns(state ?? new TemplateRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static CapabilityTemplateGrain CreateGrain(TemplateRegistryState? state = null)
    {
        var persistentState = CreatePersistentState(state);
        return new CapabilityTemplateGrain(NullLogger<CapabilityTemplateGrain>.Instance, persistentState);
    }

    private static CapabilityTemplate CreateValidTemplate(string id = "tpl-1", string name = "Research Assistant") => new()
    {
        TemplateId = TemplateId.From(id),
        Name = name,
        Description = "A research assistant template",
        Version = "1.0.0",
        Author = "test-author",
        AgentDefinition = new AgentDefinition
        {
            Model = "claude-sonnet-4-20250514",
            Tools = ["web-search"]
        },
        RequiredTools = new Dictionary<string, ToolDefinition>
        {
            ["web-search"] = new() { Type = "mcp" }
        },
        Tags = ["research", "assistant"]
    };

    [Fact]
    public async Task RegisterAsync_StoresAsDraft()
    {
        var grain = CreateGrain();
        var template = CreateValidTemplate();

        var result = await grain.RegisterAsync(template);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.TemplateId.ShouldBe(template.TemplateId);

        var stored = await grain.GetAsync(template.TemplateId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(TemplateStatus.Draft);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_PublishesValidTemplate()
    {
        var grain = CreateGrain();
        var template = CreateValidTemplate();
        await grain.RegisterAsync(template);

        var result = await grain.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Published);
        result.PublishedAt.ShouldNotBeNull();
        result.ValidationResults.ShouldAllBe(r => r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenModelMissing()
    {
        var grain = CreateGrain();
        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.From("tpl-nomodel"),
            Name = "No Model",
            Description = "Missing model",
            Version = "1.0.0",
            Author = "test-author",
            AgentDefinition = new AgentDefinition
            {
                Model = "",
                Tools = []
            }
        };
        await grain.RegisterAsync(template);

        var result = await grain.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "AgentModelPresent" && !r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenToolNotInRequiredTools()
    {
        var grain = CreateGrain();
        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.From("tpl-missingtool"),
            Name = "Missing Tool",
            Description = "Tool not in required tools",
            Version = "1.0.0",
            Author = "test-author",
            AgentDefinition = new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                Tools = ["web-search", "code-runner"]
            },
            RequiredTools = new Dictionary<string, ToolDefinition>
            {
                ["web-search"] = new() { Type = "mcp" }
            }
        };
        await grain.RegisterAsync(template);

        var result = await grain.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "ToolPresent:code-runner" && !r.Passed);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var grain = CreateGrain();

        var result = await grain.GetAsync(TemplateId.From("nonexistent"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_FindsByNameKeywords()
    {
        var grain = CreateGrain();
        var template = CreateValidTemplate();
        await grain.RegisterAsync(template);
        await grain.ValidateAndPublishAsync(template.TemplateId);

        var results = await grain.SearchAsync("Research");

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Research Assistant");
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsPublished()
    {
        var grain = CreateGrain();
        var draft = CreateValidTemplate("tpl-draft", "Draft Template");
        await grain.RegisterAsync(draft);

        var published = CreateValidTemplate("tpl-pub", "Published Template");
        await grain.RegisterAsync(published);
        await grain.ValidateAndPublishAsync(published.TemplateId);

        var results = await grain.SearchAsync("Template");

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Published Template");
    }

    [Fact]
    public async Task ListPublishedAsync_ReturnsPaginated()
    {
        var grain = CreateGrain();

        for (var i = 0; i < 5; i++)
        {
            var tpl = CreateValidTemplate($"tpl-{i}", $"Template {i}");
            await grain.RegisterAsync(tpl);
            await grain.ValidateAndPublishAsync(tpl.TemplateId);
        }

        var page1 = await grain.ListPublishedAsync(offset: 0, limit: 3);
        page1.Count.ShouldBe(3);

        var page2 = await grain.ListPublishedAsync(offset: 3, limit: 3);
        page2.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeprecateAsync_SetsDeprecatedStatus()
    {
        var grain = CreateGrain();
        var template = CreateValidTemplate();
        await grain.RegisterAsync(template);
        await grain.ValidateAndPublishAsync(template.TemplateId);

        await grain.DeprecateAsync(template.TemplateId);

        var result = await grain.GetAsync(template.TemplateId);
        result.ShouldNotBeNull();
        result.Status.ShouldBe(TemplateStatus.Deprecated);
    }

    [Fact]
    public async Task IncrementInstantiationCountAsync_Increments()
    {
        var grain = CreateGrain();
        var template = CreateValidTemplate();
        await grain.RegisterAsync(template);

        await grain.IncrementInstantiationCountAsync(template.TemplateId);
        await grain.IncrementInstantiationCountAsync(template.TemplateId);

        var result = await grain.GetAsync(template.TemplateId);
        result.ShouldNotBeNull();
        result.InstantiationCount.ShouldBe(2);
    }
}
