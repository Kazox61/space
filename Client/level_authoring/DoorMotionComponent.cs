using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>How a door slides: its placed transform is the closed pose, and it opens by <see cref="OpenOffset"/>.</summary>
[Tool, GlobalClass]
public partial class DoorMotionComponent : EntityComponent {
	/// <summary>From the closed to the open position, in the door's own frame (meters).</summary>
	[Export]
	public Vector3 OpenOffset { get; set; } = new(0f, 0f, 4f);

	/// <summary>Meters per second.</summary>
	[Export(PropertyHint.Range, "0.1,20,0.1")]
	public float Speed { get; set; } = 2f;

	[Export]
	public bool StartsOpen { get; set; }

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.DoorMotion;

	public override void Apply(EntityPlacementBuilder builder) {
		if (!OpenOffset.IsFinite() || !float.IsFinite(Speed)) {
			throw new InvalidDataException("Door offset and speed must be finite.");
		}
		builder.SetDoorMotion(
			new Fixed32.FVector3(
				Fixed32.FConversions.ToFP(OpenOffset.X),
				Fixed32.FConversions.ToFP(OpenOffset.Y),
				Fixed32.FConversions.ToFP(OpenOffset.Z)
			),
			Fixed32.FConversions.ToFP(Speed),
			StartsOpen
		);
	}
}
