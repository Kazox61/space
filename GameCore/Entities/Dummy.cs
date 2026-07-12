using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A one-hit-kill target. Kinematic (not static) so it can slide along a <see cref="PatrolRail"/> --
/// see <see cref="Core{TWorld}.DummyPatrolSystem"/>. Only the components independent of a spawn
/// position/rail -- <see cref="Body"/>'s Transform and velocity, its <see cref="Shape"/> (via
/// <c>ShapeFactory.CreateShape</c>), and <see cref="PatrolRail"/> -- are set by whichever system
/// spawns it (see <c>Core&lt;TWorld&gt;.SpawnDummySystem</c>), since <c>ShapeFactory</c> needs
/// <c>ISessionType</c>, a constraint <see cref="OnCreate{TWorld}"/> doesn't have.
/// </summary>
public struct Dummy : IEntityType {
	public byte Id() => 3;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			new ViewId { Value = ViewAsset.Dummy },
			new Health { Value = 1, MaxValue = 1 },
			new Body { Type = BodyType.Kinematic }
		);
	}
}
