using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Grains;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class MarketplaceGrainTests
{
    private static IPersistentState<MarketplaceState> CreatePersistentState()
    {
        var state = new MarketplaceState();

        var persistentState = Substitute.For<IPersistentState<MarketplaceState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (MarketplaceGrain Grain, IEventBus EventBus) CreateGrain()
    {
        var logger = NullLogger<MarketplaceGrain>.Instance;
        var eventBus = Substitute.For<IEventBus>();
        var persistentState = CreatePersistentState();

        var grain = new MarketplaceGrain(logger, eventBus, TimeProvider.System, persistentState);
        return (grain, eventBus);
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

    private static async Task<MarketplaceItem> SubmitAndPublishAsync(MarketplaceGrain grain, MarketplaceItem? item = null)
    {
        var submitted = await grain.SubmitAsync(item ?? CreateItem());
        return await grain.PublishAsync(submitted.ItemId, ApprovedReview());
    }

    [Fact]
    public async Task SubmitAsync_StoresAsDraft()
    {
        var (grain, _) = CreateGrain();
        var item = CreateItem(id: "item-1");

        var result = await grain.SubmitAsync(item);

        result.ShouldNotBeNull();
        result.ItemId.ShouldBe(item.ItemId);
        result.Status.ShouldBe(MarketplaceItemStatus.Draft);
        result.Name.ShouldBe("MCP GitHub Connector");

        var retrieved = await grain.GetAsync(item.ItemId);
        retrieved.ShouldNotBeNull();
        retrieved!.ItemId.ShouldBe(item.ItemId);
    }

    [Fact]
    public async Task PublishAsync_SetsPublishedStatus_WhenReviewApproved()
    {
        var (grain, _) = CreateGrain();
        var item = CreateItem(id: "item-pub");
        await grain.SubmitAsync(item);

        var published = await grain.PublishAsync(item.ItemId, ApprovedReview());

        published.ShouldNotBeNull();
        published.Status.ShouldBe(MarketplaceItemStatus.Published);
        published.PublishedAt.ShouldNotBeNull();
        published.SecurityReview.ShouldNotBeNull();
        published.SecurityReview!.Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_ThrowsWhenReviewNotApproved()
    {
        var (grain, _) = CreateGrain();
        var item = CreateItem(id: "item-rej");
        await grain.SubmitAsync(item);

        await Should.ThrowAsync<InvalidOperationException>(
            () => grain.PublishAsync(item.ItemId, RejectedReview()));
    }

    [Fact]
    public async Task SearchAsync_FindsByNameKeywords()
    {
        var (grain, _) = CreateGrain();
        await SubmitAndPublishAsync(grain, CreateItem(id: "item-s1", name: "Kubernetes Deploy Tool"));

        var results = await grain.SearchAsync("kubernetes", null);

        results.ShouldNotBeEmpty();
        results[0].Name.ShouldBe("Kubernetes Deploy Tool");
    }

    [Fact]
    public async Task SearchAsync_FiltersByCategory()
    {
        var (grain, _) = CreateGrain();
        await SubmitAndPublishAsync(grain, CreateItem(
            id: "item-cat1", category: MarketplaceItemCategory.ToolConnector, name: "Connector A"));
        await SubmitAndPublishAsync(grain, CreateItem(
            id: "item-cat2", category: MarketplaceItemCategory.AgentSkill, name: "Skill B"));

        var results = await grain.SearchAsync(null, MarketplaceItemCategory.AgentSkill);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Skill B");
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsPublished()
    {
        var (grain, _) = CreateGrain();
        // Submit but do not publish
        await grain.SubmitAsync(CreateItem(id: "item-draft", name: "Draft Item"));
        // Submit and publish
        await SubmitAndPublishAsync(grain, CreateItem(id: "item-live", name: "Live Item"));

        var results = await grain.SearchAsync(null, null);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Live Item");
    }

    [Fact]
    public async Task RateAsync_UpdatesRunningAverage()
    {
        var (grain, _) = CreateGrain();
        var item = CreateItem(id: "item-rate");
        await grain.SubmitAsync(item);

        await grain.RateAsync(item.ItemId, 5.0);
        var after1 = await grain.GetAsync(item.ItemId);
        after1.ShouldNotBeNull();
        after1!.Rating.ShouldBe(5.0);
        after1.RatingCount.ShouldBe(1);

        await grain.RateAsync(item.ItemId, 3.0);
        var after2 = await grain.GetAsync(item.ItemId);
        after2.ShouldNotBeNull();
        after2!.Rating.ShouldBe(4.0, 0.001);
        after2.RatingCount.ShouldBe(2);
    }

    [Fact]
    public async Task IncrementInstallCountAsync_IncrementsCount()
    {
        var (grain, _) = CreateGrain();
        var item = CreateItem(id: "item-install");
        await grain.SubmitAsync(item);

        await grain.IncrementInstallCountAsync(item.ItemId);
        await grain.IncrementInstallCountAsync(item.ItemId);

        var result = await grain.GetAsync(item.ItemId);
        result.ShouldNotBeNull();
        result!.InstallCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeprecateAsync_SetsDeprecatedStatus()
    {
        var (grain, _) = CreateGrain();
        var item = await SubmitAndPublishAsync(grain, CreateItem(id: "item-dep"));

        await grain.DeprecateAsync(item.ItemId);

        var result = await grain.GetAsync(item.ItemId);
        result.ShouldNotBeNull();
        result!.Status.ShouldBe(MarketplaceItemStatus.Deprecated);
    }

    [Fact]
    public async Task GetPublishedAsync_ReturnsPaginated()
    {
        var (grain, _) = CreateGrain();
        for (var i = 0; i < 5; i++)
            await SubmitAndPublishAsync(grain, CreateItem(id: $"item-page-{i}", name: $"Tool {i}"));

        var page1 = await grain.GetPublishedAsync(offset: 0, limit: 3);
        page1.Count.ShouldBe(3);

        var page2 = await grain.GetPublishedAsync(offset: 3, limit: 3);
        page2.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var (grain, _) = CreateGrain();

        var result = await grain.GetAsync(MarketplaceItemId.From("nonexistent"));

        result.ShouldBeNull();
    }
}
