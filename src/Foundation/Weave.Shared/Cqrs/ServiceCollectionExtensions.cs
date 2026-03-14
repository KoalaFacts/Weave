using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Weave.Shared.Cqrs;

public static class ServiceCollectionExtensions
{
    [RequiresUnreferencedCode("CQRS handler registration uses reflection to scan assemblies.")]
    public static IServiceCollection AddCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddSingleton<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton<IQueryDispatcher, QueryDispatcher>();

        foreach (var assembly in assemblies)
        {
            RegisterHandlers(services, assembly, typeof(ICommandHandler<,>));
            RegisterHandlers(services, assembly, typeof(IQueryHandler<,>));
        }

        return services;
    }

    [RequiresUnreferencedCode("CQRS handler registration uses reflection to scan assemblies.")]
    private static void RegisterHandlers(IServiceCollection services, Assembly assembly, Type handlerType)
    {
        var types = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerType)
                .Select(i => (ServiceType: i, ImplementationType: t)));

        foreach (var (serviceType, implementationType) in types)
        {
            services.AddScoped(serviceType, implementationType);
        }
    }
}
