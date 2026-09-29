using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Runtime state of one switchable navigation zone (see <see cref="LevelNavigation.Zones"/>), set by
/// gameplay: a door closing sets <see cref="Blocked"/>. <c>NavZoneApplySystem</c> writes it into the
/// world's navmesh before agents plan, so rollback restores the zone with the entity and the mesh
/// follows. <c>LevelLoader</c> spawns one open zone entity per zone.
/// </summary>
public struct NavZoneState : IComponent {
	/// <summary>Index into <see cref="LevelNavigation.Zones"/>.</summary>
	public int Zone;

	/// <summary>No corridor may enter the zone's triangles.</summary>
	public bool Blocked;

	/// <summary>Multiplies the baked A* cost of the zone's triangles. Values of zero or below count as one.</summary>
	public FP CostMultiplier;

	public static NavZoneState Open(int zone) {
		return new NavZoneState { Zone = zone, CostMultiplier = FP.One };
	}
}
