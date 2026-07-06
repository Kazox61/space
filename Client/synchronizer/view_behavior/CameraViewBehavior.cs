using Godot;
using FFS.Libraries.StaticEcs;
using Fixed64;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

[GlobalClass]
public partial class CameraViewBehavior : EntityBehavior {
	[Export] private Camera3D _camera;

	public override void OnEntityAssigned(EntityGID entityGid) {
		OnEntityUpdate(entityGid);
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
	}


	public override void OnEntityUpdate(EntityGID entityGid) {
		if (!entityGid.TryUnpack<ClientWorld>(out var entity)) {
			return;
		}

		ref readonly var playerInfo = ref entity.Read<PlayerInfo>();

		_camera.Current = playerInfo.InputChannel == CLNT.Channel;
	}
}
