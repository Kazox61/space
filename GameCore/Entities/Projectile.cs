using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public struct Projectile : IEntityType {
	public byte Id() => 2;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			new ViewId { Value = ViewAsset.Projectile },
			new Transform { Rotation = FQuaternion.Identity },
			new Velocity()
		);
	}
}
