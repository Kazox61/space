using Fixed64;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct Player : IEntityType {
	public byte Id() => 1;

	public Guid PlayerGuid;
	public ushort InputChannel;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		// The player has no Body -- it's driven by PlayerMoverSystem via CharacterMover, which
		// exists outside the rigid-body simulation (see box3d's Character Mover docs). Transform is
		// sole-owned by PlayerMoverSystem; nothing else may write it.
		// Transform.Position is the capsule's CENTER, not its feet -- matching box3d's own
		// CharacterMover sample (samples/sample.cpp) and every other Body-owning entity in this
		// project (Shape.MakeSphere/MakeBox are always given a local center of zero, so a Body's own
		// Transform.Position already *is* that shape's center -- see SpawnSphereSystem). Y=1.5 puts
		// the capsule's feet (origin + Center1.Y - Radius = origin - 1.0) exactly at the ground's top
		// surface (0.5), matching the pogo suspension's own rest height (see
		// CharacterMover.UpdatePogoGrounding), so the player doesn't visibly drop or rise on spawn.
		var transform = new Transform { Position = new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), Rotation = FQuaternion.Identity };

		entity.Set(
			new PlayerInfo { PlayerGuid = PlayerGuid, InputChannel = InputChannel },
			new Health { Value = 10, MaxValue = 10 },
			new ViewId { Value = ViewAsset.Player },
			transform,
			// Standing capsule: two half-radius hemispheres a body-length apart, centered on the origin.
			new Mover {
				CapsuleCenter1 = new Fixed32.FVector3(Fixed32.FP.Zero, -Fixed32.FP.Half, Fixed32.FP.Zero),
				CapsuleCenter2 = new Fixed32.FVector3(Fixed32.FP.Zero, Fixed32.FP.Half, Fixed32.FP.Zero),
				CapsuleRadius = Fixed32.FP.Half,
			}
		);
	}
}
