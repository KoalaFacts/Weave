using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentActorEpisodeExtractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);

    private static AgentState BaseState() => new()
    {
        AgentId = "ws-1/researcher",
        AgentName = "researcher",
        WorkspaceId = WorkspaceId.From("ws-1"),
        Status = AgentStatus.Active
    };

    private static AgentTaskInfo TaskWithProof(string description, params ProofItem[] items) => new()
    {
        TaskId = AgentTaskId.New(),
        Description = description,
        Status = AgentTaskStatus.Accepted,
        Proof = new ProofOfWork
        {
            Items = items.ToList(),
            ReviewFeedback = "shipped"
        }
    };

    [Fact]
    public void ExtractEpisodeFromTask_ReturnsEpisodeWithTitleAndDecisions()
    {
        var state = BaseState();
        state.History.Add(new ConversationMessage { Role = "user", Content = "Please ship the canary." });
        state.History.Add(new ConversationMessage { Role = "assistant", Content = "Canary at 5%." });
        var task = TaskWithProof(
            "Roll out v1.2 canary deployment",
            new ProofItem { Type = ProofType.PullRequest, Label = "PR-101", Value = "Merged" },
            new ProofItem { Type = ProofType.Custom, Label = "kubectl", Value = "rollout ok" });

        var episode = AgentActor.ExtractEpisodeFromTask(task, state, Now);

        episode.ShouldNotBeNull();
        episode.Title.ShouldBe("Roll out v1.2 canary deployment");
        episode.AgentName.ShouldBe("researcher");
        episode.SourceTaskId.ShouldBe(task.TaskId.ToString());
        episode.OccurredAt.ShouldBe(Now);
        episode.Tags.ShouldContain("researcher");
        episode.Tags.ShouldContain("PR-101");
        episode.Decisions.Count.ShouldBe(2);
        episode.Decisions.ShouldContain(d => d.Question.Contains("PullRequest") && d.ChosenOption == "Merged");
        episode.Narrative.ShouldContain("Please ship the canary.");
    }

    [Fact]
    public void ExtractEpisodeFromTask_TruncatesLongTitle()
    {
        var state = BaseState();
        var longDescription = new string('x', 200);
        var task = TaskWithProof(longDescription, new ProofItem { Type = ProofType.CiStatus, Label = "ci", Value = "green" });

        var episode = AgentActor.ExtractEpisodeFromTask(task, state, Now);

        episode.ShouldNotBeNull();
        episode.Title.Length.ShouldBe(100);
    }

    [Fact]
    public void ExtractEpisodeFromTask_ReturnsNullForBlankDescription()
    {
        var state = BaseState();
        var task = TaskWithProof("", new ProofItem { Type = ProofType.CiStatus, Label = "ci", Value = "green" });

        var episode = AgentActor.ExtractEpisodeFromTask(task, state, Now);

        episode.ShouldBeNull();
    }

    [Fact]
    public void ExtractEpisodeFromTask_OnlyIncludesHistorySinceLastEpisodeIndex()
    {
        var state = BaseState();
        state.History.Add(new ConversationMessage { Role = "user", Content = "old turn" });
        state.History.Add(new ConversationMessage { Role = "assistant", Content = "old reply" });
        state.LastEpisodeHistoryIndex = 2;
        state.History.Add(new ConversationMessage { Role = "user", Content = "fresh request" });
        var task = TaskWithProof("Latest task", new ProofItem { Type = ProofType.CiStatus, Label = "ci", Value = "green" });

        var episode = AgentActor.ExtractEpisodeFromTask(task, state, Now);

        episode.ShouldNotBeNull();
        episode.Narrative.ShouldContain("fresh request");
        episode.Narrative.ShouldNotContain("old turn");
    }

    [Fact]
    public void ExtractEpisodeFromSession_ReturnsNullWhenAllHistoryAlreadyFlushed()
    {
        var state = BaseState();
        state.History.Add(new ConversationMessage { Role = "user", Content = "already flushed" });
        state.LastEpisodeHistoryIndex = 1;

        var episode = AgentActor.ExtractEpisodeFromSession(state, Now);

        episode.ShouldBeNull();
    }

    [Fact]
    public void ExtractEpisodeFromSession_BuildsEpisodeFromUnflushedTurns()
    {
        var state = BaseState();
        state.History.Add(new ConversationMessage { Role = "user", Content = "What's our deployment cadence?" });
        state.History.Add(new ConversationMessage { Role = "assistant", Content = "Weekly canaries." });

        var episode = AgentActor.ExtractEpisodeFromSession(state, Now);

        episode.ShouldNotBeNull();
        episode.AgentName.ShouldBe("researcher");
        episode.Title.ShouldBe("What's our deployment cadence?");
        episode.Tags.ShouldContain("researcher");
        episode.Narrative.ShouldContain("Weekly canaries.");
        episode.SourceTaskId.ShouldBeNull();
        episode.OccurredAt.ShouldBe(Now);
    }
}
