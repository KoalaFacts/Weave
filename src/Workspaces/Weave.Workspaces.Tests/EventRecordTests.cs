using Weave.Shared.Ids;
using Weave.Workspaces.Events;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Coverage for domain-event record types that have no behavior beyond
/// property init — tests their construction + property plumbing so the
/// coverage baseline reflects real production usage (these events are
/// emitted by actors and consumed by subscribers).
/// </summary>
public sealed class EventRecordTests
{
    [Fact]
    public void TemplateRegisteredEvent_SetsRequiredProperties()
    {
        var evt = new TemplateRegisteredEvent
        {
            SourceId = "global",
            TemplateId = TemplateId.From("tpl-1"),
            Name = "Research Assistant",
            Author = "tests"
        };

        evt.TemplateId.ShouldBe(TemplateId.From("tpl-1"));
        evt.Name.ShouldBe("Research Assistant");
        evt.Author.ShouldBe("tests");
        evt.EventId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void TemplatePublishedEvent_SetsRequiredProperties()
    {
        var evt = new TemplatePublishedEvent
        {
            SourceId = "global",
            TemplateId = TemplateId.From("tpl-2"),
            Name = "Published Template"
        };

        evt.TemplateId.ShouldBe(TemplateId.From("tpl-2"));
        evt.Name.ShouldBe("Published Template");
    }

    [Fact]
    public void TemplateInstantiatedEvent_SetsRequiredProperties()
    {
        var evt = new TemplateInstantiatedEvent
        {
            SourceId = "global",
            TemplateId = TemplateId.From("tpl-3"),
            WorkspaceId = WorkspaceId.From("ws-1"),
            AgentName = "instantiated-agent"
        };

        evt.TemplateId.ShouldBe(TemplateId.From("tpl-3"));
        evt.WorkspaceId.ShouldBe(WorkspaceId.From("ws-1"));
        evt.AgentName.ShouldBe("instantiated-agent");
    }

    [Fact]
    public void WorkspaceStartedEvent_AgentNamesDefaultsToEmptyList()
    {
        var evt = new WorkspaceStartedEvent
        {
            SourceId = "ws-1",
            WorkspaceName = "my-workspace"
        };

        evt.AgentNames.ShouldBeEmpty();
        evt.WorkspaceName.ShouldBe("my-workspace");
    }

    [Fact]
    public void WorkspaceStartedEvent_AcceptsAgentNames()
    {
        var evt = new WorkspaceStartedEvent
        {
            SourceId = "ws-1",
            WorkspaceName = "my-workspace",
            AgentNames = ["researcher", "coder"]
        };

        evt.AgentNames.ShouldBe(["researcher", "coder"]);
    }

    [Fact]
    public void WorkspaceStoppedEvent_Minimal()
    {
        var evt = new WorkspaceStoppedEvent { SourceId = "ws-1" };

        evt.SourceId.ShouldBe("ws-1");
        evt.EventId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WorkspaceErrorEvent_CarriesErrorMessage()
    {
        var evt = new WorkspaceErrorEvent
        {
            SourceId = "ws-1",
            ErrorMessage = "runtime crashed"
        };

        evt.ErrorMessage.ShouldBe("runtime crashed");
    }
}
