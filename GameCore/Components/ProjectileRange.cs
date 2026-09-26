using FFS.Libraries.StaticEcs;
using Fixed32;

namespace Space.GameCore;

/// <summary>Distance the projectile may still travel before it dies.</summary>
public struct ProjectileRange : IComponent {
	public FP Remaining;
	public bool ExpirySent;
}
