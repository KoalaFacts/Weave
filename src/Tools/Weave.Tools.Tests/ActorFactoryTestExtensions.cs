namespace Weave.Tools.Tests;

internal static class ActorFactoryTestExtensions
{
    public static TActor GetActor<TActor>(this IActorFactory actorFactory, string key, string? classNamePrefix = null)
        where TActor : class, IVirtualActorWithStringKey =>
        actorFactory.GetGrain<TActor>(key, classNamePrefix);
}
