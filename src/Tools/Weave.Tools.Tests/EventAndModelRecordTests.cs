using Weave.Shared.Ids;
using Weave.Tools.Tool;
using Weave.Tools.Marketplace;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

/// <summary>
/// Coverage for the record types in Weave.Tools.Events and Models.
/// These are simple data carriers whose lines go uncovered until
/// someone instantiates them — record property getters/setters count
/// as lines. One instantiation per record is enough to hit them.
/// </summary>
public static class EventAndModelRecordTests
{
    public sealed class MarketplaceEvents
    {
        [Fact]
        public void ItemSubmittedEvent_round_trips_fields()
        {
            var e = new MarketplaceItemSubmittedEvent
            {
                SourceId = "src",
                ItemId = MarketplaceItemId.From("item-1"),
                Name = "My Skill",
                Author = "acme"
            };

            e.ItemId.Value.ShouldBe("item-1");
            e.Name.ShouldBe("My Skill");
            e.Author.ShouldBe("acme");
            e.EventId.ShouldNotBeNullOrWhiteSpace();
            e.Timestamp.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        }

        [Fact]
        public void ItemPublishedEvent_round_trips_fields()
        {
            var e = new MarketplaceItemPublishedEvent
            {
                SourceId = "src",
                ItemId = MarketplaceItemId.From("item-1"),
                Name = "Published"
            };

            e.ItemId.Value.ShouldBe("item-1");
            e.Name.ShouldBe("Published");
        }

        [Fact]
        public void ItemDeprecatedEvent_round_trips_fields()
        {
            var e = new MarketplaceItemDeprecatedEvent
            {
                SourceId = "src",
                ItemId = MarketplaceItemId.From("item-1"),
                Name = "Deprecated"
            };

            e.ItemId.Value.ShouldBe("item-1");
        }
    }

    public sealed class ToolEvents
    {
        [Fact]
        public void InvocationCompletedEvent_round_trips_fields()
        {
            var duration = TimeSpan.FromMilliseconds(250);
            var e = new ToolInvocationCompletedEvent
            {
                SourceId = "src",
                ToolName = "web-search",
                WorkspaceId = WorkspaceId.From("ws-1"),
                Success = true,
                Duration = duration
            };

            e.ToolName.ShouldBe("web-search");
            e.Success.ShouldBeTrue();
            e.Duration.ShouldBe(duration);
        }

        [Fact]
        public void InvocationBlockedEvent_carries_reason()
        {
            var e = new ToolInvocationBlockedEvent
            {
                SourceId = "src",
                ToolName = "shell",
                WorkspaceId = WorkspaceId.From("ws-1"),
                Reason = "capability denied"
            };

            e.Reason.ShouldBe("capability denied");
        }
    }

    public sealed class SecurityReviewRecord
    {
        [Fact]
        public void SecurityReview_defaults_ReviewedAt_to_now()
        {
            var before = DateTimeOffset.UtcNow.AddSeconds(-1);

            var review = new SecurityReview
            {
                ReviewerId = "reviewer-1",
                Approved = true
            };

            review.ReviewedAt.ShouldBeGreaterThan(before);
            review.Notes.ShouldBeNull();
        }

        [Fact]
        public void SecurityReview_with_notes_and_rejection()
        {
            var review = new SecurityReview
            {
                ReviewerId = "reviewer-2",
                Approved = false,
                Notes = "Missing sandbox isolation"
            };

            review.Approved.ShouldBeFalse();
            review.Notes.ShouldBe("Missing sandbox isolation");
        }
    }
}
