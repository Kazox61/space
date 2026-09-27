using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>
/// A switchable navigation zone in a map: a door, gate or bridge whose ground the simulation can close
/// or make costlier at runtime. <see cref="LevelExporter"/> turns it into a <see cref="NavZoneVolume"/>;
/// the navigation bake then splits the navmesh along the box, so closing the zone closes exactly the
/// ground inside it. Use its node transform for position and rotation (rotate it only about Y) and
/// <see cref="Size"/> for its extent; the box must enclose the floor it switches.
/// </summary>
/// <remarks>
/// Authoring data only: in the editor it draws its box as blue lines, in the game it draws nothing. It
/// does not collide; give a door its own collider.
/// </remarks>
[Tool]
[GlobalClass]
public partial class NavZone : Node3D {
	private static readonly Color OutlineColor = new(0.35f, 0.75f, 1f);

	private BoxOutline _outline;

	/// <summary>Gameplay refers to the zone by this id; unique within the map. Empty uses the node name.</summary>
	[Export]
	public string ZoneId { get; set; } = "";

	/// <summary>Full size in meters, centered on the node.</summary>
	[Export]
	public Vector3 Size { get; set; } = new(2f, 2f, 2f);

	public NavZoneVolume Export(string sourcePath) {
		if (!Size.IsFinite() || Size.X <= 0f || Size.Y <= 0f || Size.Z <= 0f) {
			throw new InvalidDataException($"{sourcePath}: navigation zone size must be finite and positive.");
		}
		var zone = new NavZoneVolume(
			string.IsNullOrWhiteSpace(ZoneId) ? Name.ToString() : ZoneId,
			LevelMarkers.ToFixed(GlobalTransform, sourcePath),
			new Fixed32.FVector3(
				Fixed32.FConversions.ToFP(Size.X * 0.5f),
				Fixed32.FConversions.ToFP(Size.Y * 0.5f),
				Fixed32.FConversions.ToFP(Size.Z * 0.5f)
			)
		);
		try {
			zone.Validate();
		} catch (InvalidDataException exception) {
			throw new InvalidDataException($"{sourcePath}: {exception.Message}", exception);
		}
		return zone;
	}

	public override void _Ready() {
		SetProcess(Engine.IsEditorHint());
	}

	public override void _Process(double delta) {
		_outline ??= new BoxOutline(this, OutlineColor);
		_outline.Update(Size);
	}
}
