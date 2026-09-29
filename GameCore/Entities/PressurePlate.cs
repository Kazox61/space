using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A map-placed pressure plate. Its static <see cref="Body"/> and sensor box <see cref="Shape"/> are
/// added by <c>LevelLoader</c>. It has no view: the map scene draws it.
/// </summary>
public struct PressurePlate : IEntityType {
	public int Zone;

	public byte Id() => 7;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(new PressurePlateState { Zone = Zone });
	}
}
