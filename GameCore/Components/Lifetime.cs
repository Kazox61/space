using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Countdown in seconds before the entity dies via <see cref="DeadEvent"/> (routed through
/// <c>DeathSystem</c>'s full teardown). Currently only set on projectiles by
/// <c>Core&lt;TWorld&gt;.ShootSystem</c> so misses despawn instead of flying -- with live bodies,
/// broad-phase proxies, and rollback snapshot payload -- forever.
/// </summary>
public struct Lifetime : IComponent {
	public FP TimeRemaining;
}
