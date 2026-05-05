using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Registry;

namespace Weave.Workspaces.Tests;

public sealed class CapabilityTemplateActorTests
{
    private static IActorState<TemplateRegistryState> CreatePersistentState(TemplateRegistryState? state = null)
    {
        var persistentState = Substitute.For<IActorState<TemplateRegistryState>>();
        persistentState.State.Returns(state ?? new TemplateRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return persistentState;
    }

    private static CapabilityTemplateActor CreateActor(TemplateRegistryState? state = null)
    {
        var persistentState = CreatePersistentState(state);
        return new CapabilityTemplateActor(TimeProvider.System, NullLogger<CapabilityTemplateActor>.Instance, persistentState);
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
            Tools = ["web-search"],
            Capabilities = ["tool:web-search"]
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
        var actor = CreateActor();
        var template = CreateValidTemplate();

        var result = await actor.RegisterAsync(template);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.TemplateId.ShouldBe(template.TemplateId);

        var stored = await actor.GetAsync(template.TemplateId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(TemplateStatus.Draft);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_PublishesValidTemplate()
    {
        var actor = CreateActor();
        var template = CreateValidTemplate();
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Published);
        result.PublishedAt.ShouldNotBeNull();
        result.ValidationResults.ShouldAllBe(r => r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenModelMissing()
    {
        var actor = CreateActor();
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
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "AgentModelPresent" && !r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenToolNotInRequiredTools()
    {
        var actor = CreateActor();
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
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "ToolPresent:code-runner" && !r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenToolGrantMissing()
    {
        var actor = CreateActor();
        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.From("tpl-nograntformcp"),
            Name = "No Grant For MCP",
            Description = "Tool present in RequiredTools but no capability grant covers it",
            Version = "1.0.0",
            Author = "test-author",
            AgentDefinition = new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                Tools = ["web-search"],
                Capabilities = []
            },
            RequiredTools = new Dictionary<string, ToolDefinition>
            {
                ["web-search"] = new() { Type = "mcp" }
            }
        };
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "ToolCapabilityGranted:web-search" && !r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_AcceptsToolWildcardGrant()
    {
        var actor = CreateActor();
        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.From("tpl-wildcard"),
            Name = "Wildcard Grant",
            Description = "tool:* covers every tool the agent references",
            Version = "1.0.0",
            Author = "test-author",
            AgentDefinition = new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                Tools = ["web-search", "files"],
                Capabilities = ["tool:*"]
            },
            RequiredTools = new Dictionary<string, ToolDefinition>
            {
                ["web-search"] = new() { Type = "mcp" },
                ["files"] = new() { Type = "filesystem" }
            }
        };
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Published);
        result.ValidationResults.ShouldAllBe(r => r.Passed);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_FailsWhenRequiredCapabilityNotGranted()
    {
        var actor = CreateActor();
        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.From("tpl-missingrequired"),
            Name = "Missing Required Capability",
            Description = "Template declares it needs skill:write but the agent has no covering grant",
            Version = "1.0.0",
            Author = "test-author",
            AgentDefinition = new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                Tools = [],
                Capabilities = ["skill:read"]
            },
            RequiredCapabilities = ["skill:write"]
        };
        await actor.RegisterAsync(template);

        var result = await actor.ValidateAndPublishAsync(template.TemplateId);

        result.Status.ShouldBe(TemplateStatus.Draft);
        result.ValidationResults.ShouldContain(r => r.Check == "RequiredCapabilityGranted:skill:write" && !r.Passed);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var actor = CreateActor();

        var result = await actor.GetAsync(TemplateId.From("nonexistent"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_FindsByNameKeywords()
    {
        var actor = CreateActor();
        var template = CreateValidTemplate();
        await actor.RegisterAsync(template);
        await actor.ValidateAndPublishAsync(template.TemplateId);

        var results = await actor.SearchAsync("Research");

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Research Assistant");
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsPublished()
    {
        var actor = CreateActor();
        var draft = CreateValidTemplate("tpl-draft", "Draft Template");
        await actor.RegisterAsync(draft);

        var published = CreateValidTemplate("tpl-pub", "Published Template");
        await actor.RegisterAsync(published);
        await actor.ValidateAndPublishAsync(published.TemplateId);

        var results = await actor.SearchAsync("Template");

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Published Template");
    }

    [Fact]
    public async Task ListPublishedAsync_ReturnsPaginated()
    {
        var actor = CreateActor();

        for (var i = 0; i < 5; i++)
        {
            var tpl = CreateValidTemplate($"tpl-{i}", $"Template {i}");
            await actor.RegisterAsync(tpl);
            await actor.ValidateAndPublishAsync(tpl.TemplateId);
        }

        var page1 = await actor.ListPublishedAsync(offset: 0, limit: 3);
        page1.Count.ShouldBe(3);

        var page2 = await actor.ListPublishedAsync(offset: 3, limit: 3);
        page2.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeprecateAsync_SetsDeprecatedStatus()
    {
        var actor = CreateActor();
        var template = CreateValidTemplate();
        await actor.RegisterAsync(template);
        await actor.ValidateAndPublishAsync(template.TemplateId);

        await actor.DeprecateAsync(template.TemplateId);

        var result = await actor.GetAsync(template.TemplateId);
        result.ShouldNotBeNull();
        result.Status.ShouldBe(TemplateStatus.Deprecated);
    }

    [Fact]
    public async Task IncrementInstantiationCountAsync_Increments()
    {
        var actor = CreateActor();
        var template = CreateValidTemplate();
        await actor.RegisterAsync(template);

        await actor.IncrementInstantiationCountAsync(template.TemplateId);
        await actor.IncrementInstantiationCountAsync(template.TemplateId);

        var result = await actor.GetAsync(template.TemplateId);
        result.ShouldNotBeNull();
        result.InstantiationCount.ShouldBe(2);
    }
}
