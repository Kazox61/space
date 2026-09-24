using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>A queued projectile release synchronized to the character's attack animation.</summary>
public struct PendingShot : IComponent {
	public FVector2 Aim;
	public FP TimeRemaining;
	/// <summary>Tick the attack input arrived on; the stable key for the shot's <see cref="FxEvent"/>s.</summary>
	public int AttackTick;
}
