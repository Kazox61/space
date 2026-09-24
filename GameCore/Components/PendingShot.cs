using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>A queued projectile release synchronized to the character's attack animation.</summary>
public struct PendingShot : IComponent {
	public FVector2 Aim;
	public FP TimeRemaining;
}
