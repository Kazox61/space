using Fixed;
using Fixed32;

namespace Space.GameCore;

/// <summary>A placement component; its value is the component's bit in the level file's presence mask.</summary>
public enum LevelEntityComponentKind : byte {
	Health,
	Loot,
	Body,
	BoxShape,
	View,
	Navigation,
	/// <summary>The id of the <see cref="NavZoneVolume"/> a door blocks or a pressure plate switches.</summary>
	ZoneLink,
	DoorMotion,
}

public sealed class EntityPlacementBuilder {
	private readonly string _sourcePath;
	private readonly LevelEntityType _type;
	private readonly FWorldTransform _transform;
	private PlacementComponents _components;

	public EntityPlacementBuilder(string sourcePath, LevelEntityType type, FWorldTransform transform) {
		_sourcePath = sourcePath;
		_type = type;
		_transform = transform;
	}

	public void SetHealth(int health) => _components = _components with { Health = health };
	public void SetLoot(LootKind loot) => _components = _components with { Loot = loot };
	public void SetBody(BodyType bodyType) => _components = _components with { Body = bodyType };
	public void SetBoxShape(FVector3 halfExtents, FP density) => _components = _components with { BoxShape = new BoxShapeData(halfExtents, density) };
	public void SetView(ViewAsset view) => _components = _components with { View = view };
	public void SetNavigation(NavContribution navigation) => _components = _components with { Navigation = navigation };
	public void SetZoneLink(string zoneId) => _components = _components with { ZoneLink = zoneId };
	public void SetDoorMotion(FVector3 openOffset, FP speed, bool startsOpen) => _components = _components with { DoorMotion = new DoorMotionData(openOffset, speed, startsOpen) };

	/// <summary>The placement with the components that were set; validation rejects missing and forbidden ones.</summary>
	public EntityPlacement Build() {
		var placement = new EntityPlacement(_sourcePath, _type, _transform, _components);
		LevelDataValidation.Validate(placement);
		return placement;
	}
}
