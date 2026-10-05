using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A map-placed sliding door. Its kinematic <see cref="Body"/> and box <see cref="Shape"/> are added by
/// <c>LevelLoader</c>, since <c>ShapeFactory</c> needs <c>ISessionType</c>.
/// </summary>
public struct Door : IEntityType {
	public DoorState State;

	public byte Id() => 6;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			State,
			new ViewId { Value = ViewAsset.Door }
		);
	}
}
