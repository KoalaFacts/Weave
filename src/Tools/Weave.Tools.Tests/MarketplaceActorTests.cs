using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Actors;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class MarketplaceActorTests
{
    private static IActorState<MarketplaceState> CreatePersistentState()
    {
        var state = new MarketplaceState();

        var persistentState = Substitute.For<IActorState<MarketplaceState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (MarketplaceActor Actor, IEventBus EventBus) CreateActor()
    {
        var logger = NullLogger<MarketplaceActor>.Instance;
        var eventBus = Substitute.For<IEventBus>();
        var persistentState = CreatePersistentState();

        var actor = new MarketplaceActor(logger, eventBus, TimeProvider.System, persistentState);
        return (actor, eventBus);
    }

    private static MarketplaceItem CreateItem(
        string? id = null,
        string name = "MCP GitHub Connector",
        string description = "Connect to GitHub via MCP",
        MarketplaceItemCategory category = MarketplaceItemCategory.ToolConnector,
        string version = "1.0.0",
        string author = "weave-team",
        List<string>? tags = null)
    {
        return new MarketplaceItem
        {
            ItemId = MarketplaceItemId.From(id ?? Guid.NewGuid().ToString("N")),
            Name = name,
            Description = description,
            Category = category,
            Version = version,
            Author = author,
            Tags = tags ?? ["github", "mcp", "vcs"]
        };
    }

    private static SecurityReview ApprovedReview() => new()
    {
        ReviewerId = "security-team",
        Approved = true,
        Notes = "All checks passed"
    };

    private static SecurityReview RejectedReview() => new()
    {
        ReviewerId = "security-team",
        Approved = false,
        Notes = "Failed security audit"
    };

    private static async Task<MarketplaceItem> SubmitAndPublishAsync(MarketplaceActor actor, MarketplaceItem? item = null)
    {
        var submitted = await actor.SubmitAsync(item ?? CreateItem());
        return await actor.PublishAsync(submitted.ItemId, ApprovedReview());
    }

    [Fact]
    public async Task SubmitAsync_StoresAsDraft()
    {
        var (actor, _) = CreateActor();
        var item = CreateItem(id: "item-1");

        var result = await actor.SubmitAsync(item);

        result.ShouldNotBeNull();
        result.ItemId.ShouldBe(item.ItemId);
        result.Status.ShouldBe(MarketplaceItemStatus.Draft);
        result.Name.ShouldBe("MCP GitHub Connector");

        var retrieved = await actor.GetAsync(item.ItemId);
        retrieved.ShouldNotBeNull();
        retrieved!.ItemId.ShouldBe(item.ItemId);
    }

    [Fact]
    public async Task PublishAsync_SetsPublishedStatus_WhenReviewApproved()
    {
        var (actor, _) = CreateActor();
        var item = CreateItem(id: "item-pub");
        await actor.SubmitAsync(item);

        var published = await actor.PublishAsync(item.ItemId, ApprovedReview());

        published.ShouldNotBeNull();
        published.Status.ShouldBe(MarketplaceItemStatus.Published);
        published.PublishedAt.ShouldNotBeNull();
        published.SecurityReview.ShouldNotBeNull();
        published.SecurityReview!.Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_ThrowsWhenReviewNotApproved()
    {
        var (actor, _) = CreateActor();
        var item = CreateItem(id: "item-rej");
        await actor.SubmitAsync(item);

        await Should.ThrowAsync<InvalidOperationException>(
            () => actor.PublishAsync(item.ItemId, RejectedReview()));
    }

    [Fact]
    public async Task SearchAsync_FindsByNameKeywords()
    {
        var (actor, _) = CreateActor();
        await SubmitAndPublishAsync(actor, CreateItem(id: "item-s1", name: "Kubernetes Deploy Tool"));

        var results = await actor.SearchAsync("kubernetes", null);

        results.ShouldNotBeEmpty();
        results[0].Name.ShouldBe("Kubernetes Deploy Tool");
    }

    [Fact]
    public async Task SearchAsync_FiltersByCategory()
    {
        var (actor, _) = CreateActor();
        await SubmitAndPublishAsync(actor, CreateItem(
            id: "item-cat1", category: MarketplaceItemCategory.ToolConnector, name: "Connector A"));
        await SubmitAndPublishAsync(actor, CreateItem(
            id: "item-cat2", category: MarketplaceItemCategory.AgentSkill, name: "Skill B"));

        var results = await actor.SearchAsync(null, MarketplaceItemCategory.AgentSkill);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Skill B");
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsPublished()
    {
        var (actor, _) = CreateActor();
        // Submit but do not publish
        await actor.SubmitAsync(CreateItem(id: "item-draft", name: "Draft Item"));
        // Submit and publish
        await SubmitAndPublishAsync(actor, CreateItem(id: "item-live", name: "Live Item"));

        var results = await actor.SearchAsync(null, null);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Live Item");
    }

    [Fact]
    public async Task RateAsync_UpdatesRunningAverage()
    {
        var (actor, _) = CreateActor();
        var item = CreateItem(id: "item-rate");
        await actor.SubmitAsync(item);

        await actor.RateAsync(item.ItemId, 5.0);
        var after1 = await actor.GetAsync(item.ItemId);
        after1.ShouldNotBeNull();
        after1!.Rating.ShouldBe(5.0);
        after1.RatingCount.ShouldBe(1);

        await actor.RateAsync(item.ItemId, 3.0);
        var after2 = await actor.GetAsync(item.ItemId);
        after2.ShouldNotBeNull();
        after2!.Rating.ShouldBe(4.0, 0.001);
        after2.RatingCount.ShouldBe(2);
    }

    [Fact]
    public async Task IncrementInstallCountAsync_IncrementsCount()
    {
        var (actor, _) = CreateActor();
        var item = CreateItem(id: "item-install");
        await actor.SubmitAsync(item);

        await actor.IncrementInstallCountAsync(item.ItemId);
        await actor.IncrementInstallCountAsync(item.ItemId);

        var result = await actor.GetAsync(item.ItemId);
        result.ShouldNotBeNull();
        result!.InstallCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeprecateAsync_SetsDeprecatedStatus()
    {
        var (actor, _) = CreateActor();
        var item = await SubmitAndPublishAsync(actor, CreateItem(id: "item-dep"));

        await actor.DeprecateAsync(item.ItemId);

        var result = await actor.GetAsync(item.ItemId);
        result.ShouldNotBeNull();
        result!.Status.ShouldBe(MarketplaceItemStatus.Deprecated);
    }

    [Fact]
    public async Task GetPublishedAsync_ReturnsPaginated()
    {
        var (actor, _) = CreateActor();
        for (var i = 0; i < 5; i++)
            await SubmitAndPublishAsync(actor, CreateItem(id: $"item-page-{i}", name: $"Tool {i}"));

        var page1 = await actor.GetPublishedAsync(offset: 0, limit: 3);
        page1.Count.ShouldBe(3);

        var page2 = await actor.GetPublishedAsync(offset: 3, limit: 3);
        page2.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var (actor, _) = CreateActor();

        var result = await actor.GetAsync(MarketplaceItemId.From("nonexistent"));

        result.ShouldBeNull();
    }
}
