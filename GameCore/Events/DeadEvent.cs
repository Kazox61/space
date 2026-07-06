using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct DeadEvent : IEvent {
	public EntityGID Gid;
}
