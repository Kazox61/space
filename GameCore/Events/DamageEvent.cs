using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct DamageEvent : IEvent {
	public int Amount;
	public EntityGID Target;
	public EntityGID Source;
}
