using Fixed;
using Fixed32;
using Space.GameCore;

namespace PhysicsSmokeTest;

public static class TestLevels {
	/// <summary>The former hardcoded test arena: an 80x1x80 ground (top at y=0.5) and a 4x1x4 box behind spawn.</summary>
	public static readonly LevelData Arena = new([
		StaticBox(
			"Arena/Ground",
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			new FVector3(40.ToFP(), FP.Half, 40.ToFP())
		),
		StaticBox(
			"Arena/Box",
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity),
			new FVector3(2.ToFP(), FP.Half, 2.ToFP())
		),
	]);

	/// <summary>A static geometry placement: a static box with density 1, as the static recipe authors it.</summary>
	public static EntityPlacement StaticBox(
		string path,
		FWorldTransform transform,
		FVector3 halfExtents,
		NavContribution navigation = NavContribution.Walkable
	) => new(
		path,
		LevelEntityType.StaticGeometry,
		transform,
		new PlacementComponents(
			Body: BodyType.Static,
			BoxShape: new BoxShapeData(halfExtents, FP.One),
			Navigation: navigation
		)
	);

	/// <summary>A door placement: a kinematic box with a door view, linked to <paramref name="zoneId"/>.</summary>
	public static EntityPlacement Door(
		string path,
		FWorldTransform closed,
		FVector3 halfExtents,
		string zoneId,
		FVector3 openOffset,
		FP speed,
		bool startsOpen
	) => new(
		path,
		LevelEntityType.Door,
		closed,
		new PlacementComponents(
			Body: BodyType.Kinematic,
			BoxShape: new BoxShapeData(halfExtents, FP.One),
			View: ViewAsset.Door,
			ZoneLink: zoneId,
			DoorMotion: new DoorMotionData(openOffset, speed, startsOpen)
		)
	);

	/// <summary>A pressure plate placement: a static sensor box linked to <paramref name="zoneId"/>.</summary>
	public static EntityPlacement PressurePlate(string path, FWorldTransform transform, FVector3 halfExtents, string zoneId) => new(
		path,
		LevelEntityType.PressurePlate,
		transform,
		new PlacementComponents(
			Body: BodyType.Static,
			BoxShape: new BoxShapeData(halfExtents, FP.One),
			ZoneLink: zoneId
		)
	);

	public static EntityPlacement Platform(
		string path,
		FWorldTransform start,
		FVector3 halfExtents,
		string zoneId,
		FVector3 travelOffset,
		FP speed,
		bool startsAtEnd = false
	) => new(
		path,
		LevelEntityType.Platform,
		start,
		new PlacementComponents(
			Body: BodyType.Kinematic,
			BoxShape: new BoxShapeData(halfExtents, FP.One),
			View: ViewAsset.Platform,
			Navigation: NavContribution.Walkable,
			ZoneLink: zoneId,
			RailMotion: new RailMotionData(travelOffset, speed, startsAtEnd)
		)
	);
}
