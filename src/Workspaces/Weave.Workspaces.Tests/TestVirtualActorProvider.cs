using Weave.Shared.VirtualActors;

namespace Weave.Workspaces.Tests;

internal sealed class TestVirtualActorProvider(IActorFactory actorFactory) : IVirtualActorProvider
{
    private static readonly System.Reflection.MethodInfo StringKeyGetActorMethod =
        typeof(IActorFactory)
            .GetMethods()
            .Single(static method =>
                method.Name == "GetGrain"
                && method.IsGenericMethodDefinition
                && method.GetParameters() is
                [
                    { ParameterType: var primaryKeyType },
                    { ParameterType: var classNamePrefixType }
                ]
                && primaryKeyType == typeof(string)
                && classNamePrefixType == typeof(string));

    public TActor GetActor<TActor>(VirtualActorId id)
        where TActor : class
    {
        return (TActor)StringKeyGetActorMethod
            .MakeGenericMethod(typeof(TActor))
            .Invoke(actorFactory, [id.Value, null])!;
    }
}
