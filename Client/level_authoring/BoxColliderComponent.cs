using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class BoxColliderComponent : ColliderComponent {
	private Vector3 _size = Vector3.One;

	/// <summary>Full size in meters, centered on the collider node. Each side may be at most 80 m.</summary>
	[Export]
	public Vector3 Size {
		get => _size;
		set {
			_size = value;
			EmitChanged();
		}
	}

	public override LevelColliderComponentKind Kind => LevelColliderComponentKind.Box;

	public override void Apply(StaticBoxBuilder builder) {
		if (!Size.IsFinite()) {
			throw new InvalidDataException("Box size must be finite.");
		}
		builder.SetBox(new Fixed32.FVector3(
			Fixed32.FConversions.ToFP(Size.X * 0.5f),
			Fixed32.FConversions.ToFP(Size.Y * 0.5f),
			Fixed32.FConversions.ToFP(Size.Z * 0.5f)
		));
	}
}
