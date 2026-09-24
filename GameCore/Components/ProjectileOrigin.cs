using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Who fired a projectile and on which tick, as plain values. Unlike the <see cref="Shooter"/> link
/// these survive a rollback that re-creates the projectile or its shooter under new entity IDs, so
/// they key the <see cref="FxKind.ProjectileHit"/> effect (see docs/rollback-presentation-events.md §4.4).
/// </summary>
public struct ProjectileOrigin : IComponent {
	public int SpawnTick;
	public ushort Channel;
}
