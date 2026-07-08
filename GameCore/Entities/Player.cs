using Fixed64;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct Player : IEntityType {
	public byte Id() => 1;

	public Guid PlayerGuid;
	public ushort InputChannel;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		var transform = new Transform { Position = new FVector3(FP.Zero, 40.ToFP(), FP.Zero), Rotation = FQuaternion.Identity };

		entity.Set(
			new PlayerInfo { PlayerGuid = PlayerGuid, InputChannel = InputChannel },
			new Health { Value = 10, MaxValue = 10 },
			new ViewId { Value = ViewAsset.Player },
			transform,
			new JumpState { VerticalVelocity = FP.Zero, Grounded = true },
			new Body { Type = BodyType.Kinematic, Transform = transform.ToWorldTransform() }
		);
	}
}
