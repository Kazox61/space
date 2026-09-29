using Fixed;
using Fixed32;

namespace Space.GameCore;

public enum LevelEntityType : byte {
	Crate = 1,
	/// <summary>Immovable level geometry (walls, floors) with a static body; the navmesh bake's input.</summary>
	StaticGeometry = 2,
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

public readonly record struct BoxShapeData(FVector3 HalfExtents, FP Density);

/// <summary>
/// The components a placement carries; null when absent. Which ones an entity type requires or forbids is
/// checked by validation.
/// </summary>
public readonly record struct PlacementComponents(
	int? Health = null,
	LootKind? Loot = null,
	BodyType? Body = null,
	BoxShapeData? BoxShape = null,
	ViewAsset? View = null,
	NavContribution? Navigation = null
) {
	public bool Has(LevelEntityComponentKind kind) => kind switch {
		LevelEntityComponentKind.Health => Health.HasValue,
		LevelEntityComponentKind.Loot => Loot.HasValue,
		LevelEntityComponentKind.Body => Body.HasValue,
		LevelEntityComponentKind.BoxShape => BoxShape.HasValue,
		LevelEntityComponentKind.View => View.HasValue,
		LevelEntityComponentKind.Navigation => Navigation.HasValue,
		_ => false,
	};
}

public readonly record struct EntityPlacement(
	string SourcePath,
	LevelEntityType Type,
	FWorldTransform Transform,
	PlacementComponents Components
);

/// <summary>
/// A box the navmesh bake rasterizes, derived from a <see cref="LevelEntityType.StaticGeometry"/> placement;
/// see <see cref="LevelData.NavigationSources"/>.
/// </summary>
public readonly record struct NavSourceBox(
	string SourcePath,
	FWorldTransform Transform,
	FVector3 HalfExtents,
	NavContribution Navigation
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

	/// <summary>
	/// The <see cref="LevelEntityType.StaticGeometry"/> placements as bake input, ordinal by source path.
	/// Placements without a box shape or navigation are left out; validation rejects them.
	/// </summary>
	public IReadOnlyList<NavSourceBox> NavigationSources { get; }

	/// <summary>Switchable navigation zones, the bake input for <see cref="LevelNavigation.Zones"/>.</summary>
	public IReadOnlyList<NavZoneVolume> NavZones { get; }

	/// <summary>The baked navmesh, or null for a level without navigation.</summary>
	public LevelNavigation? Navigation { get; }

	public LevelData(
		IEnumerable<EntityPlacement> entities,
		LevelNavigation? navigation = null,
		IEnumerable<NavZoneVolume>? navZones = null
	) {
		Entities = entities.ToArray();
		NavigationSources = Entities
			.Where(static placement => placement.Type == LevelEntityType.StaticGeometry
				&& placement.Components is { BoxShape: not null, Navigation: not null })
			.Select(static placement => new NavSourceBox(
				placement.SourcePath,
				placement.Transform,
				placement.Components.BoxShape!.Value.HalfExtents,
				placement.Components.Navigation!.Value
			))
			.OrderBy(static source => source.SourcePath, StringComparer.Ordinal)
			.ToArray();
		NavZones = navZones?.ToArray() ?? [];
		Navigation = navigation;
	}

	public LevelData WithNavigation(LevelNavigation? navigation) => new(Entities, navigation, NavZones);
}
