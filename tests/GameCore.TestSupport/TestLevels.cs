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
}
