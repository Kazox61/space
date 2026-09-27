using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A navigation-controlled character: the player's capsule and <see cref="CharacterMoverSystem"/>
/// movement, steered by a <see cref="NavAgent"/> instead of input. Its <see cref="Transform"/> and
/// <see cref="NavAgent"/> are set by the spawner (see <c>Core&lt;TWorld&gt;.SpawnNavCharacterSystem</c>).
/// Like the player it has no Body or Shape, so projectiles and other characters pass through it.
/// </summary>
public struct NavCharacter : IEntityType {
	public byte Id() => 5;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			new ViewId { Value = ViewAsset.NavCharacter },
			// Same standing capsule as the player (see Player.cs), matching NavBakeSettings.Default's agent.
			new Mover {
				CapsuleCenter1 = new Fixed32.FVector3(Fixed32.FP.Zero, -Fixed32.FP.Half, Fixed32.FP.Zero),
				CapsuleCenter2 = new Fixed32.FVector3(Fixed32.FP.Zero, Fixed32.FP.Half, Fixed32.FP.Zero),
				CapsuleRadius = Fixed32.FP.Half,
			},
			new CharacterMoveIntent()
		);
	}
}
