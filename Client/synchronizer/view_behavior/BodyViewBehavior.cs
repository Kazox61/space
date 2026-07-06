using Godot;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;
using Fixed;

namespace Space.Client;

[GlobalClass]
public partial class BodyViewBehavior : EntityBehavior {
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

		ref readonly var body = ref entity.Read<Body>();

		_targetNodePosition.Position = new Vector3(
			Fixed64.FConversions.ToFloat(body.Transform.Position.X),
			Fixed64.FConversions.ToFloat(body.Transform.Position.Y),
			Fixed64.FConversions.ToFloat(body.Transform.Position.Z)
		);
		_targetNodeRotation.Quaternion = new Quaternion(
			body.Transform.Rotation.X.ToFloat(),
			body.Transform.Rotation.Y.ToFloat(),
			body.Transform.Rotation.Z.ToFloat(),
			body.Transform.Rotation.W.ToFloat()
		);
	}
}
