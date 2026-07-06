using Godot;
using FFS.Libraries.StaticEcs;
using Fixed64;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

[GlobalClass]
public partial class TransformViewBehavior : EntityBehavior {
	[Export] private Node3D _targetNodePosition;
	[Export] private Node3D _targetNodeRotation;

	public override void OnEntityAssigned(EntityGID entityGid) {
		OnEntityUpdate(entityGid);
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
	}


	public override void OnEntityUpdate(EntityGID entityGid) {
		if (!entityGid.TryUnpack<ClientWorld>(out var entity)) {
			return;
		}

		ref readonly var transform = ref entity.Read<Transform>();

		_targetNodePosition.Position = new Vector3(
			transform.Position.X.ToFloat(),
			transform.Position.Y.ToFloat(),
			transform.Position.Z.ToFloat()
		);
		_targetNodeRotation.Quaternion = new Quaternion(
			transform.Rotation.X.ToFloat(),
			transform.Rotation.Y.ToFloat(),
			transform.Rotation.Z.ToFloat(),
			transform.Rotation.W.ToFloat()
		);
	}
}
