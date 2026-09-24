using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Queues a projectile whenever a player's attack input (<see cref="PlayerInput.AttackX"/>/
	/// <see cref="PlayerInput.AttackY"/>, set by <c>ClientGame.OnAttack</c> client-side and zeroed again
	/// after one tick) carries a direction, then releases it after <see cref="CharacterRes.AttackDelay"/>.
	/// Mirrors <see cref="PlayerMoverSystem"/>'s XZ mapping of a 2D input into a 3D world direction.
	/// Reports <see cref="FxKind.AttackStarted"/> when queuing and <see cref="FxKind.ShotReleased"/> when
	/// releasing, both keyed by the attack tick.
	/// </summary>
	public struct ShootSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity playerEntity, ref PlayerInfo playerInfo, ref Transform transform) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				// The attack is edge-triggered: a predicted tick carries the last input forward (Aged), so
				// reading it with LastFresh would queue a ghost shot on every predicted tick after a remote
				// player's attack arrives.
				var attackInput = input.FreshOrDefault();

				var aim = Fixed64.FVector2.NormalizeSafe(new Fixed64.FVector2(attackInput.AttackX, attackInput.AttackY));
				if (Fixed64.FVector2.LengthSqr(aim) < Fixed64.FP.CalculationsEpsilonSqr) {
					return;
				}

				var delay = Systems.GetResource<CharacterRes>().AttackDelay;
				var queuedShot = W.NewEntity<Default>().Set(new PendingShot {
					Aim = aim,
					TimeRemaining = delay,
					AttackTick = S.CurrentTick,
				});
				queuedShot.Set(new W.Link<Shooter>(playerEntity));

				RecordFx(new FxEvent {
					Kind = FxKind.AttackStarted,
					Channel = playerInfo.InputChannel,
					KeyTick = S.CurrentTick,
					Position = transform.Position,
					Direction = new Fixed64.FVector3(aim.X, Fixed64.FP.Zero, aim.Y),
				});
			});

			foreach (var queuedShot in W.Query<All<PendingShot, W.Link<Shooter>>>().Entities()) {
				ref var pending = ref queuedShot.Ref<PendingShot>();
				ref readonly var shooterLink = ref queuedShot.Read<W.Link<Shooter>>();
				pending.TimeRemaining -= Const.DeltaTime;
				if (pending.TimeRemaining > Fixed64.FP.Zero) {
					continue;
				}

				if (!shooterLink.Value.TryUnpack<TWorld>(out var playerEntity)
					|| !playerEntity.Has<PlayerInfo>() || !playerEntity.Has<Transform>()) {
					queuedShot.Destroy();
					continue;
				}

				ref readonly var playerInfo = ref playerEntity.Read<PlayerInfo>();
				ref readonly var transform = ref playerEntity.Read<Transform>();
				var direction = new Fixed64.FVector3(pending.Aim.X, Fixed64.FP.Zero, pending.Aim.Y);
				var res = Systems.GetResource<CharacterRes>();

				// Spawn a bit ahead of the player's own center so the muzzle isn't buried in their capsule.
				var spawnPosition = transform.Position + direction * Fixed64.FP.FromRatio(3, 4);
				var rotation = Fixed64.FQuaternion.LookRotation(direction, Fixed64.FVector3.Up);
				var worldTransform = new FWorldTransform(new FPos(spawnPosition.X, spawnPosition.Y, spawnPosition.Z), rotation.To32());

				var projectileTransform = new Transform();
				projectileTransform.SetFromWorldTransform(worldTransform);

				var projectile = W.NewEntity<Projectile>();
				projectile.Set(projectileTransform);
				projectile.Set(new W.Link<Shooter>(playerEntity));
				projectile.Set(new Lifetime { TimeRemaining = res.ProjectileLifetime });
				projectile.Set(new ProjectileOrigin { SpawnTick = S.CurrentTick, Channel = playerInfo.InputChannel });

				BodyOperations.CreateBody(projectile, BodyType.Kinematic, worldTransform);
				BodyOperations.SetBullet(projectile, true);
				BodyOperations.SetLinearVelocity(projectile, (direction * res.ProjectileSpeed).To32());

				var shape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 4));
				shape.EnableContactEvents = true;
				// So this projectile never collides with its own shooter once players have a Shape too.
				shape.Filter.GroupIndex = Filter.SelfGroup(playerInfo.InputChannel);
				ShapeFactory.CreateShape(projectile, shape);

				RecordFx(new FxEvent {
					Kind = FxKind.ShotReleased,
					Channel = playerInfo.InputChannel,
					KeyTick = pending.AttackTick,
					Position = spawnPosition,
					Direction = direction,
				});
				queuedShot.Destroy();
			}
		}
	}
}
