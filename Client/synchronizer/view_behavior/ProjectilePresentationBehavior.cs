using FFS.Libraries.StaticEcs;
using Fixed64;
using Godot;
using Space.GameCore;

namespace Space.Client;

[GlobalClass]
public partial class ProjectilePresentationBehavior : EntityBehavior {
	[Export] private AudioStream _hitSound;

	private FP _lastLifetime;

	public override void OnEntityAssigned(EntityGID entityGid) {
		_lastLifetime = FP.Zero;
		OnEntityUpdate(entityGid);
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
		// ProjectileHitSystem kills on contact; ProjectileDespawnSystem kills once Lifetime runs out.
		// Anything with more than a couple of ticks left was destroyed by a hit.
		if (_hitSound is not null && _lastLifetime > Space.GameCore.Const.DeltaTime * 2) {
			Audio.PlaySfx(_hitSound);
		}
		_lastLifetime = FP.Zero;
	}

	public override void OnEntityUpdate(EntityGID entityGid) {
		if (entityGid.TryUnpack<ClientWorld>(out var entity) && entity.Has<Lifetime>()) {
			_lastLifetime = entity.Read<Lifetime>().TimeRemaining;
		}
	}
}
