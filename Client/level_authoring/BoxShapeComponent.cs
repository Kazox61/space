using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class BoxShapeComponent : EntityComponent {
	[Export]
	public Vector3 Size { get; set; } = Vector3.One;

	[Export(PropertyHint.Range, "0.001,2,0.001")]
	public float Density { get; set; } = 1f;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.BoxShape;

	public override void Apply(EntityPlacementBuilder builder) {
		if (!Size.IsFinite() || !float.IsFinite(Density)) {
			throw new InvalidDataException("Box size and density must be finite.");
		}
		builder.SetBoxShape(
			new Fixed32.FVector3(
				Fixed32.FConversions.ToFP(Size.X * 0.5f),
				Fixed32.FConversions.ToFP(Size.Y * 0.5f),
				Fixed32.FConversions.ToFP(Size.Z * 0.5f)
			),
			Fixed32.FConversions.ToFP(Density)
		);
	}
}
