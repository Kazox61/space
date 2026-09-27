using Fixed;
using Fixed32;

namespace Space.GameCore;

public enum LevelEntityType : byte {
	Crate = 1,
}

public enum LootKind : byte {
	None,
	Ammo,
	Health,
}

public enum NavContribution : byte {
	Walkable = 0,
	ObstacleOnly = 1,
	Excluded = 2,
}

public readonly record struct CratePlacementData(
	int Health,
	LootKind Loot,
	BodyType BodyType,
	FVector3 BoxHalfExtents,
	FP Density,
	ViewAsset View
);

public readonly record struct EntityPlacement(
	string SourcePath,
	LevelEntityType Type,
	FWorldTransform Transform,
	CratePlacementData Crate
);

/// <summary>An immovable box collider: walls, floors and other static level geometry.</summary>
public readonly record struct StaticBox(
	string SourcePath,
	FWorldTransform Transform,
	FVector3 HalfExtents,
	NavContribution Navigation = NavContribution.Walkable
) {
	public void Validate() => LevelDataValidation.Validate(this);
}

/// <summary>
/// A box volume whose navmesh triangles the simulation can switch off or make costlier at runtime
/// (a door, gate or bridge). The bake splits the navmesh along it; see <see cref="NavZoneData"/>.
/// </summary>
public readonly record struct NavZoneVolume(
	string Id,
	FWorldTransform Transform,
	FVector3 HalfExtents
) {
	public void Validate() => LevelDataValidation.Validate(this);
}

public sealed class LevelData {
	public static readonly LevelData Empty = new([]);

	public IReadOnlyList<EntityPlacement> Entities { get; }
	public IReadOnlyList<StaticBox> StaticBoxes { get; }

	/// <summary>Switchable navigation zones, the bake input for <see cref="LevelNavigation.Zones"/>.</summary>
	public IReadOnlyList<NavZoneVolume> NavZones { get; }

	/// <summary>The baked navmesh, or null for a level without navigation.</summary>
	public LevelNavigation? Navigation { get; }

	public LevelData(
		IEnumerable<EntityPlacement> entities,
		IEnumerable<StaticBox>? staticBoxes = null,
		LevelNavigation? navigation = null,
		IEnumerable<NavZoneVolume>? navZones = null
	) {
		Entities = entities.ToArray();
		StaticBoxes = staticBoxes?.ToArray() ?? [];
		NavZones = navZones?.ToArray() ?? [];
		Navigation = navigation;
	}

	public LevelData WithNavigation(LevelNavigation? navigation) => new(Entities, StaticBoxes, navigation, NavZones);
}
