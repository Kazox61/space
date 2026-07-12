using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct Projectile : IEntityType {
	public byte Id() => 2;

	/// <summary>
	/// Only the components independent of a spawn position/direction. <see cref="Body"/>'s Transform,
	/// its <see cref="Shape"/> (via <c>ShapeFactory.CreateShape</c>), and initial
	/// <see cref="Body.LinearVelocity"/> are set by whichever system spawns the projectile (see
	/// <c>Core&lt;TWorld&gt;.ShootSystem</c>) -- <c>ShapeFactory</c> lives on <c>Core&lt;TWorld&gt;</c>,
	/// which needs <c>ISessionType</c>, a constraint <see cref="OnCreate{TWorld}"/> doesn't have.
	/// A kinematic <see cref="Body"/> moves in a straight line on its own (see
	/// <c>ContactSolverSystem.IntegratePositions</c>/<c>FinalizeBodies</c>) -- no dedicated movement
	/// system needed.
	/// </summary>
	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			new ViewId { Value = ViewAsset.Projectile },
			new Body { Type = BodyType.Kinematic }
		);
		entity.Set<IsProjectile>();
	}
}
