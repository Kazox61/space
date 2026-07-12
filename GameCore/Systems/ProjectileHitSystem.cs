using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Reacts to <see cref="ContactBeginTouchEvent"/> (sent by <see cref="ContactSystem"/>) to turn a
	/// projectile touching something into gameplay effects: damages whatever it hit (if it has
	/// <see cref="Health"/>) and always kills the projectile itself, one hit and done.
	/// </summary>
	public struct ProjectileHitSystem : ISystem {
		private EventReceiver<TWorld, ContactBeginTouchEvent> receiver;

		public void Init() {
			receiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();
		}

		public void Update() {
			foreach (var e in receiver) {
				if (!TryResolveOwner(e.Value.ShapeA, out var ownerA) || !TryResolveOwner(e.Value.ShapeB, out var ownerB)) {
					continue;
				}

				W.Entity projectile;
				W.Entity target;
				if (ownerA.Has<IsProjectile>()) {
					projectile = ownerA;
					target = ownerB;
				} else if (ownerB.Has<IsProjectile>()) {
					projectile = ownerB;
					target = ownerA;
				} else {
					continue;
				}

				if (target.Has<Health>()) {
					// Attribute the kill to the shooter, not the projectile -- the projectile is
					// about to be destroyed, so nothing downstream could resolve it as a source.
					var source = projectile.Has<W.Link<Shooter>>() && projectile.Read<W.Link<Shooter>>().Value.TryUnpack<TWorld>(out var shooter)
						? shooter.GID
						: projectile.GID;
					W.SendEvent(new DamageEvent { Amount = int.MaxValue, Target = target.GID, Source = source });
				}

				W.SendEvent(new DeadEvent { Gid = projectile.GID });
			}
		}

		private static bool TryResolveOwner(EntityGID shapeGid, out W.Entity owner) {
			owner = default;
			return shapeGid.TryUnpack<TWorld>(out var shapeEntity)
				&& shapeEntity.Has<W.Link<BodyOwner>>()
				&& shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack(out owner);
		}
	}
}
