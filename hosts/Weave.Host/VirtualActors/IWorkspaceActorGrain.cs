using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.VirtualActors;

public interface IWorkspaceActorGrain : IWorkspaceActor, IGrainWithStringKey;
