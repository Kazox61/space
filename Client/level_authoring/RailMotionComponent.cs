using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>How a platform patrols from its placed floor to a second stop.</summary>
[Tool, GlobalClass]
public partial class RailMotionComponent : EntityComponent {
	/// <summary>From the authored floor to the other stop, in the platform's own frame (meters).</summary>
	[Export]
	public Vector3 TravelOffset { get; set; } = new(0f, 4f, 0f);

	[Export(PropertyHint.Range, "0.1,20,0.1")]
	public float Speed { get; set; } = 2f;

	[Export]
	public bool StartsAtEnd { get; set; }

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.RailMotion;

	public override void Apply(EntityPlacementBuilder builder) {
		if (!TravelOffset.IsFinite() || !float.IsFinite(Speed)) {
			throw new InvalidDataException("Rail offset and speed must be finite.");
		}
		builder.SetRailMotion(
			new Fixed32.FVector3(
				Fixed32.FConversions.ToFP(TravelOffset.X),
				Fixed32.FConversions.ToFP(TravelOffset.Y),
				Fixed32.FConversions.ToFP(TravelOffset.Z)
			),
			Fixed32.FConversions.ToFP(Speed),
			StartsAtEnd
		);
	}
}
