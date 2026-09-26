using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Reacts to discrete and continuous contact events to turn a projectile hit into gameplay
	/// effects: damages whatever it hit (if it has <see cref="Health"/>) and always kills the
	/// projectile itself, one hit and done. Reports <see cref="FxKind.ProjectileHit"/> keyed by the
	/// projectile's <see cref="ProjectileOrigin"/>, so a hit that a correction shifts by a tick, or that
	/// lands on a re-created projectile entity, is still the same effect.
	/// </summary>
	public struct ProjectileHitSystem : ISystem {
		private EventReceiver<TWorld, ContactBeginTouchEvent> receiver;
		private EventReceiver<TWorld, ContinuousHitEvent> continuousReceiver;

		public void Init() {
			receiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();
			continuousReceiver = W.RegisterEventReceiver<ContinuousHitEvent>();
		}

		public void Update() {
			foreach (var e in receiver) {
				HandleHit(e.Value.ShapeA, e.Value.ShapeB, surface: null);
			}
			foreach (var e in continuousReceiver) {
				ref readonly var hit = ref e.Value;
				var point = new FVector3(hit.Point.X, hit.Point.Y, hit.Point.Z);
				var normal = new FVector3(hit.Normal.X.To64(), hit.Normal.Y.To64(), hit.Normal.Z.To64());
				HandleHit(hit.BulletShape, hit.TargetShape, (point, normal));
			}
		}

		/// <param name="surface">Contact point and outward surface normal, when the hit reports them (continuous hits only).</param>
		private static void HandleHit(EntityGID shapeA, EntityGID shapeB, (FVector3 Point, FVector3 Normal)? surface) {
			if (!TryResolveOwner(shapeA, out var ownerA) || !TryResolveOwner(shapeB, out var ownerB)) {
				return;
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
				return;
			}

			if (target.Has<Health>()) {
				// Attribute the kill to the shooter, not the projectile -- the projectile is
				// about to be destroyed, so nothing downstream could resolve it as a source.
				var source = projectile.Has<W.Link<Shooter>>() && projectile.Read<W.Link<Shooter>>().Value.TryUnpack<TWorld>(out var shooter)
					? shooter.GID
					: projectile.GID;
				W.SendEvent(new DamageEvent { Amount = int.MaxValue, Target = target.GID, Source = source });
			}

			if (projectile.Has<ProjectileOrigin>() && projectile.Has<Transform>()) {
				ref readonly var origin = ref projectile.Read<ProjectileOrigin>();
				// Without a reported surface, face back along the flight: discrete contacts never change a
				// kinematic body's velocity. (Continuous hits do -- the solver strips its normal part -- which
				// is why they must use the reported normal.)
				var velocity = projectile.Has<Body>() ? projectile.Read<Body>().LinearVelocity : default;
				RecordFx(new FxEvent {
					Kind = FxKind.ProjectileHit,
					Channel = origin.Channel,
					KeyTick = origin.SpawnTick,
					Position = surface?.Point ?? projectile.Read<Transform>().Position,
					Direction = surface?.Normal ?? new FVector3(-velocity.X.To64(), -velocity.Y.To64(), -velocity.Z.To64()),
				});
			}

			W.SendEvent(new DeadEvent { Gid = projectile.GID });
		}

		private static bool TryResolveOwner(EntityGID shapeGid, out W.Entity owner) {
			owner = default;
			return shapeGid.TryUnpack<TWorld>(out var shapeEntity)
				&& shapeEntity.Has<W.Link<BodyOwner>>()
				&& shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack(out owner);
		}
	}
}
