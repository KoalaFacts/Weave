using Weave.Agents.Heartbeat;

namespace Weave.Silo.VirtualActors;

public interface IHeartbeatActorGrain : IHeartbeatActor, IGrainWithStringKey;
