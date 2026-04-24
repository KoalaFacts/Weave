using Weave.Shared.VirtualActors;

namespace Weave.Tools.Tests;

internal sealed class TestVirtualActorProvider(IActorFactory actorFactory) : IVirtualActorProvider
{
    public TActor GetActor<TActor>(VirtualActorId id)
        where TActor : class
    {
        return actorFactory.GetGrain<TActor>(id.Value, null);
    }
}
