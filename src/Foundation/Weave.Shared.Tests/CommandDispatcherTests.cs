using Microsoft.Extensions.DependencyInjection;
using Weave.Shared.Cqrs;

namespace Weave.Shared.Tests;

public sealed class CommandDispatcherTests
{
    private sealed record TestCommand(string Input);
    private sealed record TestQuery(string Input);

    private sealed class TestCommandHandler : ICommandHandler<TestCommand, string>
    {
        public Task<string> HandleAsync(TestCommand command, CancellationToken ct) =>
            Task.FromResult($"handled:{command.Input}");
    }

    private sealed class TestQueryHandler : IQueryHandler<TestQuery, int>
    {
        public Task<int> HandleAsync(TestQuery query, CancellationToken ct) =>
            Task.FromResult(query.Input.Length);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICommandHandler<TestCommand, string>, TestCommandHandler>();
        services.AddScoped<IQueryHandler<TestQuery, int>, TestQueryHandler>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CommandDispatcher_DispatchesToRegisteredHandler()
    {
        var sp = BuildServiceProvider();
        var dispatcher = new CommandDispatcher(sp);

        var result = await dispatcher.DispatchAsync<TestCommand, string>(
            new TestCommand("hello"), CancellationToken.None);

        result.ShouldBe("handled:hello");
    }

    [Fact]
    public async Task QueryDispatcher_DispatchesToRegisteredHandler()
    {
        var sp = BuildServiceProvider();
        var dispatcher = new QueryDispatcher(sp);

        var result = await dispatcher.DispatchAsync<TestQuery, int>(
            new TestQuery("hello"), CancellationToken.None);

        result.ShouldBe(5);
    }

    [Fact]
    public void CommandDispatcher_WithMissingHandler_ThrowsInvalidOperation()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new CommandDispatcher(sp);

        Should.Throw<InvalidOperationException>(
            () => dispatcher.DispatchAsync<TestCommand, string>(
                new TestCommand("test"), CancellationToken.None));
    }

    [Fact]
    public void QueryDispatcher_WithMissingHandler_ThrowsInvalidOperation()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new QueryDispatcher(sp);

        Should.Throw<InvalidOperationException>(
            () => dispatcher.DispatchAsync<TestQuery, int>(
                new TestQuery("test"), CancellationToken.None));
    }
}
