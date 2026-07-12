using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns a projectile whenever a player's queued attack input (<see cref="PlayerInput.AttackX"/>/
	/// <see cref="PlayerInput.AttackY"/>, set by <c>Test.OnAttack</c> client-side and zeroed again
	/// after one tick) carries a direction. Mirrors <see cref="PlayerMoverSystem"/>'s XZ mapping of a
	/// 2D input into a 3D world direction.
	/// </summary>
	public struct ShootSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity playerEntity, ref PlayerInfo playerInfo, ref Transform transform) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				var lastInput = input.LastFresh();

				var aim = Fixed64.FVector2.NormalizeSafe(new Fixed64.FVector2(lastInput.AttackX, lastInput.AttackY));
				if (Fixed64.FVector2.LengthSqr(aim) < Fixed64.FP.CalculationsEpsilonSqr) {
					return;
				}

				var direction = new Fixed64.FVector3(aim.X, Fixed64.FP.Zero, aim.Y);
				var speed = Systems.GetResource<CharacterRes>().ProjectileSpeed;

				// Spawn a bit ahead of the player's own center so the muzzle isn't buried in their capsule.
				var spawnPosition = transform.Position + direction * Fixed64.FP.FromRatio(3, 4);
				var worldTransform = new FWorldTransform(new FPos(spawnPosition.X, spawnPosition.Y, spawnPosition.Z), FQuaternion.Identity);

				var projectileTransform = new Transform();
				projectileTransform.SetFromWorldTransform(worldTransform);

				var projectile = W.NewEntity<Projectile>();
				projectile.Set(projectileTransform);
				projectile.Set(new W.Link<Shooter>(playerEntity));

				ref var body = ref projectile.Ref<Body>()!; // Projectile.OnCreate always sets Body.
				body.Transform = worldTransform;
				body.LinearVelocity = (direction * speed).To32();

				var shape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 4));
				shape.EnableContactEvents = true;
				// So this projectile never collides with its own shooter once players have a Shape too.
				shape.Filter.GroupIndex = Filter.SelfGroup(playerInfo.InputChannel);
				ShapeFactory.CreateShape(projectile, shape);
			});
		}
	}
}
