using Weave.Workspaces.Actors;

namespace Weave.Silo.VirtualActors;

public interface IWorkspaceActorGrain : IWorkspaceActor, IGrainWithStringKey;
