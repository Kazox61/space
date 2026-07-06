using FFS.Libraries.StaticEcs;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;


public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct MovementSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity entity, ref PlayerInfo playerInfo, ref Transform transform, ref JumpState jumpState) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				var moveInput = FVector2.NormalizeSafe(new FVector2(input.LastFresh().MoveX, input.LastFresh().MoveY));

				var radius = Systems.GetResource<PlanetRes>().Radius;

				var up = FVector3.Normalize(transform.Position);

				var forward = transform.Rotation * FVector3.Forward;
				forward = FVector3.NormalizeSafe(
					forward - up * FVector3.Dot(forward, up),
					FVector3.Orthonormal(up));
				var right = FVector3.Cross(up, forward);

				if (!FVector2.ApproximatelyEqual(moveInput, FVector2.Zero)) {
					var moveDir = forward * moveInput.Y + right * moveInput.X;
					moveDir = FVector3.NormalizeSafe(moveDir);

					var newPos = transform.Position + moveDir * Systems.GetResource<CharacterRes>().MoveSpeed * Const.DeltaTime;
					newPos = FVector3.Normalize(newPos) * radius;
					transform.Position = newPos;

					up = FVector3.Normalize(newPos);
					forward = FVector3.NormalizeSafe(
						forward - up * FVector3.Dot(forward, up),
						FVector3.Orthonormal(up)
					);
				}

				transform.Rotation = SafeLookRotation(forward, up);

				var jumpPressed = input.LastFresh().Jump;
				if (jumpPressed && jumpState.Grounded) {
					jumpState.VerticalVelocity = Systems.GetResource<CharacterRes>().JumpForce;
					jumpState.Grounded = false;
				}

				jumpState.VerticalVelocity -= Systems.GetResource<PhysicsRes>().Gravity * Const.DeltaTime;
				transform.Position += up * jumpState.VerticalVelocity * Const.DeltaTime;

				var height = FVector3.Length(transform.Position) - radius;
				if (height <= FP.Zero) {
					transform.Position = FVector3.Normalize(transform.Position) * radius;
					jumpState.VerticalVelocity = FP.Zero;
					jumpState.Grounded = true;
				}

				var attackInput = FVector2.NormalizeSafe(new FVector2(input.LastFresh().AttackX, input.LastFresh().AttackY));
				if (!FVector2.ApproximatelyEqual(FVector2.Zero, attackInput)) {
					var attackDir = forward * attackInput.Y + right * attackInput.X;
					attackDir = FVector3.NormalizeSafe(attackDir, forward);

					var projectile = W.NewEntity<Projectile>();

					ref var projectileTransform = ref projectile.Mut<Transform>();
					projectileTransform.Position = transform.Position + up * FP.One;
					projectileTransform.Rotation = SafeLookRotation(attackDir, up);

					ref var projectileVelocity = ref projectile.Ref<Velocity>();
					projectileVelocity.Value = attackDir * 10;
				}
			});
		}

		private static FQuaternion FromToRotation(FVector3 from, FVector3 to) {
			var dot = FVector3.Dot(from, to);

			if (dot > FP.One - FP.CalculationsEpsilon) {
				return FQuaternion.Identity;
			}

			if (dot < FP.MinusOne + FP.CalculationsEpsilon) {
				var axis = FVector3.Orthonormal(from);
				return FQuaternion.AxisAngleRadians(axis, FP.Pi);
			}

			var cross = FVector3.Cross(from, to);
			var s = FP.Sqrt((FP.One + dot) * 2);
			var invS = FP.One / s;

			return FQuaternion.Normalize(new FQuaternion(
				cross.X * invS,
				cross.Y * invS,
				cross.Z * invS,
				s * FP.Half));
		}

		private static FQuaternion SafeLookRotation(FVector3 forward, FVector3 up) {
			forward = FVector3.NormalizeSafe(forward, FVector3.Forward);
			up = FVector3.NormalizeSafe(up, FVector3.Up);

			var q1 = FromToRotation(FVector3.Forward, forward);

			var upAfterQ1 = q1 * FVector3.Up;
			var q2 = FromToRotation(upAfterQ1, up);

			return FQuaternion.Normalize(q2 * q1);
		}
	}
}
