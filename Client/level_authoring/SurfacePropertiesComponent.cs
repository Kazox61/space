using System.IO;
using Fixed32;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class SurfacePropertiesComponent : EntityComponent {
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Friction { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Restitution { get; set; }
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float RollingResistance { get; set; }
	[Export]
	public Vector3 TangentVelocity { get; set; }
	[Export(PropertyHint.Range, "0,30,0.1")]
	public float CharacterBounceSpeed { get; set; }
	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.SurfaceProperties;
	public override void Apply(EntityPlacementBuilder builder) {
		if (!float.IsFinite(Friction) || !float.IsFinite(Restitution) || !float.IsFinite(RollingResistance)
			|| !TangentVelocity.IsFinite() || !float.IsFinite(CharacterBounceSpeed)) {
			throw new InvalidDataException("Surface properties must be finite.");
		}
		var material = SurfaceMaterial.Default;
		material.Friction = Friction.ToFP();
		material.Restitution = Restitution.ToFP();
		material.RollingResistance = RollingResistance.ToFP();
		material.TangentVelocity = new FVector3(TangentVelocity.X.ToFP(), TangentVelocity.Y.ToFP(), TangentVelocity.Z.ToFP());
		builder.SetSurfaceProperties(material, CharacterBounceSpeed.ToFP());
	}
}
