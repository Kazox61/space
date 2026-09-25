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
	FVector3 HalfExtents
);

public sealed class LevelData {
	public static readonly LevelData Empty = new([]);

	public IReadOnlyList<EntityPlacement> Entities { get; }
	public IReadOnlyList<StaticBox> StaticBoxes { get; }

	public LevelData(IEnumerable<EntityPlacement> entities, IEnumerable<StaticBox>? staticBoxes = null) {
		Entities = entities.ToArray();
		StaticBoxes = staticBoxes?.ToArray() ?? [];
	}
}
