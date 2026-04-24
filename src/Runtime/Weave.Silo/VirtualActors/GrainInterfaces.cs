using Orleans;
using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Security.Actors;
using Weave.Tools.Actors;
using Weave.Workspaces.Actors;

namespace Weave.Silo.VirtualActors;

// Orleans grain interfaces that bridge domain actor interfaces to the Orleans runtime.
// Each extends the domain interface plus IGrainWithStringKey so Orleans can resolve them.

public interface IAgentActorGrain : IAgentActor, IGrainWithStringKey;
public interface IAgentSupervisorActorGrain : IAgentSupervisorActor, IGrainWithStringKey;
public interface IChannelGatewayActorGrain : IChannelGatewayActor, IGrainWithStringKey;
public interface IHeartbeatActorGrain : IHeartbeatActor, IGrainWithStringKey;
public interface IProofValidatorActorGrain : IProofValidatorActor, IGrainWithStringKey;
public interface IProofVerifierActorGrain : IProofVerifierActor, IGrainWithStringKey;
public interface ISkillMemoryActorGrain : ISkillMemoryActor, IGrainWithStringKey;
public interface IToolRegistryActorGrain : IToolRegistryActor, IGrainWithStringKey;
public interface IUserModelActorGrain : IUserModelActor, IGrainWithStringKey;
public interface IToolActorGrain : IToolActor, IGrainWithStringKey;
public interface IMarketplaceActorGrain : IMarketplaceActor, IGrainWithStringKey;
public interface ISecretProxyActorGrain : ISecretProxyActor, IGrainWithStringKey;
public interface IWorkspaceActorGrain : IWorkspaceActor, IGrainWithStringKey;
public interface IWorkspaceRegistryActorGrain : IWorkspaceRegistryActor, IGrainWithStringKey;
public interface ICapabilityTemplateActorGrain : ICapabilityTemplateActor, IGrainWithStringKey;
