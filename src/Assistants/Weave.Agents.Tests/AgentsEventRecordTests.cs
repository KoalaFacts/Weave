using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Coverage for domain-event record types in <c>Weave.Agents.Events</c>.
/// These records have no behavior beyond property init, but they're a
/// real part of the event-bus contract — verifying construction + property
/// plumbing catches accidental <c>[Id(...)]</c> renumbering and keeps the
/// baseline honest.
/// </summary>
public sealed class AgentsEventRecordTests
{
    private static readonly WorkspaceId Ws = WorkspaceId.From("ws-1");

    [Fact]
    public void AgentActivatedEvent_ExposesRequiredFields_ToolsDefaultsEmpty()
    {
        var evt = new AgentActivatedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            Model = "gpt-4o-mini"
        };

        evt.AgentName.ShouldBe("a");
        evt.WorkspaceId.ShouldBe(Ws);
        evt.Model.ShouldBe("gpt-4o-mini");
        evt.Tools.ShouldBeEmpty();
    }

    [Fact]
    public void AgentActivatedEvent_AcceptsTools()
    {
        var evt = new AgentActivatedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            Model = "m",
            Tools = ["t1", "t2"]
        };

        evt.Tools.ShouldBe(["t1", "t2"]);
    }

    [Fact]
    public void AgentDeactivatedEvent_ExposesRequiredFields()
    {
        var evt = new AgentDeactivatedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws
        };

        evt.AgentName.ShouldBe("a");
        evt.WorkspaceId.ShouldBe(Ws);
    }

    [Fact]
    public void AgentErrorEvent_CarriesErrorMessage()
    {
        var evt = new AgentErrorEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            ErrorMessage = "boom"
        };

        evt.ErrorMessage.ShouldBe("boom");
    }

    [Fact]
    public void AgentTaskCompletedEvent_ExposesTaskId()
    {
        var evt = new AgentTaskCompletedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            TaskId = AgentTaskId.From("t-1")
        };

        evt.TaskId.ShouldBe(AgentTaskId.From("t-1"));
    }

    [Fact]
    public void AgentTaskAwaitingReviewEvent_ExposesTaskId()
    {
        var evt = new AgentTaskAwaitingReviewEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            TaskId = AgentTaskId.From("t-2")
        };

        evt.TaskId.ShouldBe(AgentTaskId.From("t-2"));
    }

    [Fact]
    public void AgentTaskReviewedEvent_AcceptedAndRejected()
    {
        var accepted = new AgentTaskReviewedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            TaskId = AgentTaskId.From("t-3"),
            Accepted = true
        };
        var rejected = new AgentTaskReviewedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            TaskId = AgentTaskId.From("t-4"),
            Accepted = false
        };

        accepted.Accepted.ShouldBeTrue();
        rejected.Accepted.ShouldBeFalse();
    }

    [Fact]
    public void ProofVerifiedEvent_ExposesCountsAndFeedback()
    {
        var evt = new ProofVerifiedEvent
        {
            SourceId = "ws-1/a",
            AgentName = "a",
            WorkspaceId = Ws,
            TaskId = AgentTaskId.From("t-5"),
            Accepted = true,
            Feedback = "looks good",
            VoteCount = 3,
            AcceptCount = 2
        };

        evt.VoteCount.ShouldBe(3);
        evt.AcceptCount.ShouldBe(2);
        evt.Feedback.ShouldBe("looks good");
    }

    [Fact]
    public void SkillCreatedEvent_ExposesSkillIdAndTitle()
    {
        var id = SkillId.New();
        var evt = new SkillCreatedEvent
        {
            SourceId = "ws-1",
            WorkspaceId = Ws,
            SkillId = id,
            Title = "How to debug",
            CreatedByAgent = "researcher"
        };

        evt.SkillId.ShouldBe(id);
        evt.Title.ShouldBe("How to debug");
        evt.CreatedByAgent.ShouldBe("researcher");
    }

    [Fact]
    public void ToolConnectedEvent_ExposesFields()
    {
        var evt = new ToolConnectedEvent
        {
            SourceId = "ws-1/shell",
            ToolName = "shell",
            WorkspaceId = Ws,
            ToolType = "cli"
        };

        evt.ToolName.ShouldBe("shell");
        evt.ToolType.ShouldBe("cli");
    }

    [Fact]
    public void ToolDisconnectedEvent_ExposesFields()
    {
        var evt = new ToolDisconnectedEvent
        {
            SourceId = "ws-1/shell",
            ToolName = "shell",
            WorkspaceId = Ws
        };

        evt.ToolName.ShouldBe("shell");
    }

    [Fact]
    public void ToolErrorEvent_CarriesErrorMessage()
    {
        var evt = new ToolErrorEvent
        {
            SourceId = "ws-1/shell",
            ToolName = "shell",
            WorkspaceId = Ws,
            ErrorMessage = "connection refused"
        };

        evt.ErrorMessage.ShouldBe("connection refused");
    }

    [Fact]
    public void ChannelMessageReceivedEvent_ExposesFields()
    {
        var id = ChannelId.New();
        var evt = new ChannelMessageReceivedEvent
        {
            SourceId = "ws-1",
            WorkspaceId = Ws,
            ChannelId = id,
            SenderId = "u-1",
            AgentName = "researcher"
        };

        evt.ChannelId.ShouldBe(id);
        evt.SenderId.ShouldBe("u-1");
        evt.AgentName.ShouldBe("researcher");
    }

    [Fact]
    public void ChannelMessageSentEvent_ExposesFields()
    {
        var evt = new ChannelMessageSentEvent
        {
            SourceId = "ws-1",
            WorkspaceId = Ws,
            ChannelId = ChannelId.New(),
            AgentName = "researcher"
        };

        evt.AgentName.ShouldBe("researcher");
    }

    [Fact]
    public void ChannelRegisteredEvent_ExposesFields()
    {
        var evt = new ChannelRegisteredEvent
        {
            SourceId = "ws-1",
            WorkspaceId = Ws,
            ChannelId = ChannelId.New(),
            Type = ChannelType.Discord,
            Name = "general"
        };

        evt.Type.ShouldBe(ChannelType.Discord);
        evt.Name.ShouldBe("general");
    }

    [Fact]
    public void UserInteractionRecordedEvent_ExposesFields()
    {
        var evt = new UserInteractionRecordedEvent
        {
            SourceId = "ws-1/alice",
            WorkspaceId = Ws,
            UserId = "alice",
            AgentName = "researcher"
        };

        evt.UserId.ShouldBe("alice");
        evt.AgentName.ShouldBe("researcher");
    }
}
