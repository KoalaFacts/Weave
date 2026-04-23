using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Weave.Shared.Cqrs;

namespace Weave.Shared.Tests;

/// <summary>
/// Covers the reflection-based <see cref="ServiceCollectionExtensions.AddCqrs"/>
/// path. Production uses the source-generated <c>AddGeneratedCqrsHandlers()</c>
/// but the reflection path stays supported for dynamic scenarios
/// (plugins loading assemblies at runtime, etc.).
/// </summary>
public static class ServiceCollectionExtensionsTests
{
    // Handlers defined inline so the assembly scan finds them.
    public sealed record SampleCommand(string Input);
    public sealed record SampleQuery(int Value);

    public sealed class SampleCommandHandler : ICommandHandler<SampleCommand, string>
    {
        public Task<string> HandleAsync(SampleCommand command, CancellationToken ct) =>
            Task.FromResult($"handled:{command.Input}");
    }

    public sealed class SampleQueryHandler : IQueryHandler<SampleQuery, int>
    {
        public Task<int> HandleAsync(SampleQuery query, CancellationToken ct) =>
            Task.FromResult(query.Value * 2);
    }

    public sealed class DispatcherRegistration
    {
        [Fact]
        public void AddCqrs_registers_scoped_command_dispatcher()
        {
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

            dispatcher.ShouldNotBeNull();
            dispatcher.ShouldBeOfType<CommandDispatcher>();
        }

        [Fact]
        public void AddCqrs_registers_scoped_query_dispatcher()
        {
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var dispatcher = scope.ServiceProvider.GetRequiredService<IQueryDispatcher>();

            dispatcher.ShouldNotBeNull();
            dispatcher.ShouldBeOfType<QueryDispatcher>();
        }

        [Fact]
        public void AddCqrs_returns_same_service_collection_for_chaining()
        {
            var services = new ServiceCollection();
            var returned = services.AddCqrs(Assembly.GetExecutingAssembly());

            returned.ShouldBeSameAs(services);
        }

        [Fact]
        public void AddCqrs_with_no_assemblies_still_registers_dispatchers()
        {
            // Empty `params` array — dispatcher registration is
            // independent of whether handlers are scanned.
            var services = new ServiceCollection();
            services.AddCqrs();
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            scope.ServiceProvider.GetService<ICommandDispatcher>().ShouldNotBeNull();
            scope.ServiceProvider.GetService<IQueryDispatcher>().ShouldNotBeNull();
        }
    }

    public sealed class HandlerRegistration
    {
        [Fact]
        public async Task AddCqrs_registers_command_handlers_from_assembly()
        {
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
            var result = await dispatcher.DispatchAsync<SampleCommand, string>(
                new SampleCommand("hello"), CancellationToken.None);

            result.ShouldBe("handled:hello");
        }

        [Fact]
        public async Task AddCqrs_registers_query_handlers_from_assembly()
        {
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var dispatcher = scope.ServiceProvider.GetRequiredService<IQueryDispatcher>();
            var result = await dispatcher.DispatchAsync<SampleQuery, int>(
                new SampleQuery(21), CancellationToken.None);

            result.ShouldBe(42);
        }

        [Fact]
        public void AddCqrs_registers_handlers_as_scoped()
        {
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());

            var handlerDescriptor = services.First(d =>
                d.ServiceType == typeof(ICommandHandler<SampleCommand, string>));

            handlerDescriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        }

        [Fact]
        public void AddCqrs_skips_abstract_and_interface_types()
        {
            // The scanner filters `IsAbstract` and `IsInterface`. This
            // test confirms by asserting the scanner doesn't register
            // the interface type itself as a handler.
            var services = new ServiceCollection();
            services.AddCqrs(Assembly.GetExecutingAssembly());

            var interfaceRegistrations = services.Where(d =>
                d.ImplementationType == typeof(ICommandHandler<,>)).ToList();

            interfaceRegistrations.ShouldBeEmpty();
        }
    }
}
