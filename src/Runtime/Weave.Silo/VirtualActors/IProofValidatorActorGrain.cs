using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;

namespace Weave.Silo.VirtualActors;

public interface IProofValidatorActorGrain : IProofValidatorActor, IGrainWithStringKey;
