using Weave.Shared.VirtualActors;

namespace Weave.Silo.VirtualActors;

public sealed class OrleansVirtualActorProvider(IActorFactory actorFactory) : IVirtualActorProvider
{
    public TActor GetActor<TActor>(VirtualActorId id)
        where TActor : class
    {
        return (TActor)actorFactory.GetGrain(typeof(TActor), id.Value);
    }
}
