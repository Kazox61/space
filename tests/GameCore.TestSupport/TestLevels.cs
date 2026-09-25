using Fixed;
using Fixed32;
using Space.GameCore;

namespace PhysicsSmokeTest;

public static class TestLevels {
	/// <summary>The former hardcoded test arena: an 80x1x80 ground (top at y=0.5) and a 4x1x4 box behind spawn.</summary>
	public static readonly LevelData Arena = new([], [
		new StaticBox(
			"Arena/Ground",
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			new FVector3(40.ToFP(), FP.Half, 40.ToFP())
		),
		new StaticBox(
			"Arena/Box",
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity),
			new FVector3(2.ToFP(), FP.Half, 2.ToFP())
		),
	]);
}
