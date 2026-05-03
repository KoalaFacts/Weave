namespace Weave.Shared.VirtualActors;

public interface IVirtualActorProvider
{
    TActor GetActor<TActor>(VirtualActorId id)
        where TActor : class;
}
