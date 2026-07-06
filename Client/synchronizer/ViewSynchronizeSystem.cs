using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public class ViewSynchronizeSystem : ISystem {
	private readonly Dictionary<EntityGID, EntityView> _views = [];

	public void Update() {
		foreach (var entity in W.Query<AllAdded<ViewId>>().Entities()) {
			var viewId = entity.Read<ViewId>();
			var path = viewId.Value switch {
				ViewAsset.Player => "res://player.tscn",
				ViewAsset.Projectile => "res://projectile.tscn"
			};
			var packedScene = GD.Load<PackedScene>(path);
			var view = packedScene.Instantiate<EntityView>();
			_views[entity.GID] = view;
			view.AssignEntity(entity.GID);
		}

		List<EntityGID> toRemove = [];
		foreach (var (gid, view) in _views) {
			if (!gid.TryUnpack<ClientWorld>(out _)) {
				view.RemoveEntity(gid);
				toRemove.Add(gid);
			}
		}
		foreach (var gid in toRemove) {
			_views.Remove(gid);
		}

		foreach (var (gid, view) in _views) {
			view.UpdateEntity(gid);
		}
	}
}
