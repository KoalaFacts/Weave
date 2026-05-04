using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;

namespace Weave.Silo.VirtualActors;

public interface IAgentSupervisorActorGrain : IAgentSupervisorActor, IGrainWithStringKey;
