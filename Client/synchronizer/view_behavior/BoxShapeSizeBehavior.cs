using FFS.Libraries.StaticEcs;
using Fixed32;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

/// <summary>
/// Scales a unit-sized node to its entity's box shape, so one view scene fits boxes of any size (a
/// door's size comes from its level placement).
/// </summary>
[Tool, GlobalClass]
public partial class BoxShapeSizeBehavior : EntityBehavior {
	[Export] private Node3D _target;

	public override void OnEntityAssigned(EntityGID entityGid) {
		if (!entityGid.TryUnpack<ClientWorld>(out var entity) || !entity.Has<W.Links<Shapes>>()) {
			return;
		}
		ref readonly var shapes = ref entity.Read<W.Links<Shapes>>();
		if (shapes.Length == 0 || !shapes[0].Value.TryUnpack<ClientWorld>(out var shapeEntity) || !shapeEntity.Has<Shape>()) {
			return;
		}
		ref readonly var shape = ref shapeEntity.Read<Shape>();
		if (shape.Type == ShapeType.Sphere) {
			SetSize(Vector3.One * (shape.SphereShape.Radius.ToFloat() * 2f));
			return;
		}
		if (shape.Type != ShapeType.Hull) {
			return;
		}
		var h = shape.HullShape.HalfExtents;
		SetSize(new Vector3(h.X.ToFloat(), h.Y.ToFloat(), h.Z.ToFloat()) * 2f);
	}

	public void SetSize(Vector3 size) => _target.Scale = size;

	public override void OnEntityRemoved(EntityGID entityGid) {
		_target.Scale = Vector3.One;
	}

	public override void OnEntityUpdate(EntityGID entityGid) {
	}
}
