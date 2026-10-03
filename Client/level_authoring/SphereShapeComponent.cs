using System.IO;
using Fixed32;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class SphereShapeComponent : EntityComponent {
	[Export(PropertyHint.Range, "0.001,4,0.001")]
	public float Radius { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0.001,2,0.001")]
	public float Density { get; set; } = 1f;
	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.SphereShape;
	public override void Apply(EntityPlacementBuilder builder) {
		if (!float.IsFinite(Radius) || !float.IsFinite(Density)) {
			throw new InvalidDataException("Sphere radius and density must be finite.");
		}
		builder.SetSphereShape(Radius.ToFP(), Density.ToFP());
	}
}
