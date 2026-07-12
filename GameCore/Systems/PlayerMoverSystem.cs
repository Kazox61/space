using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Drives the player's <see cref="Mover"/> from input via box3d's Character Mover algorithm
	/// (<see cref="CharacterMover"/>/<see cref="MoverSolver"/>): collide -> solve -> cast -> move,
	/// repeated up to 5 times per tick to converge, then a one-sided push impulse onto any dynamic
	/// body the mover displaced. Writes <see cref="Transform"/> directly -- the player has no
	/// <see cref="Body"/>, so unlike Body-owning entities there's no BodyTransformSyncSystem involved
	/// here; this is the sole writer of the player's Transform.
	///
	/// Ground detection is box3d's footprint-aware ground check
	/// (<see cref="CharacterMover.UpdatePogoGrounding"/>, ported from the more complete character
	/// sample's CategorizeGround/TraceBody -- a small box sweep, not a single ray, so standing
	/// mostly off a ledge doesn't still read as grounded), computed fresh each tick from
	/// <see cref="Mover.CapsuleCenter1"/>, before the movement target is built; its spring-damper
	/// output (<see cref="Mover.PogoVelocity"/>) is added into that target alongside
	/// gravity-integrated <see cref="Mover.Velocity"/>, not just used as a boolean.
	/// </summary>
	public struct PlayerMoverSystem : ISystem {
		private static readonly FP Tolerance = FP.FromRatio(1, 100);

		public void Update() {
			W.Query().For(static (ref PlayerInfo playerInfo, ref Transform transform, ref Mover mover) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				var lastInput = input.LastFresh();
				var moveInput = Fixed64.FVector2.NormalizeSafe(new Fixed64.FVector2(lastInput.MoveX, lastInput.MoveY));
				var characterRes = Systems.GetResource<CharacterRes>();
				var moveSpeed = characterRes.MoveSpeed;
				var jumpForce = characterRes.JumpForce.To32();
				var gravity = W.GetResource<PhysicsWorld>().Gravity;
				var dt = Const.DeltaTime.To32();

				// Jump check comes before the grounded reset (and clears Grounded immediately) so the
				// fresh jump velocity isn't stomped back to zero by that same reset this tick --
				// mirrors box3d's CharacterMover::Step/SolveMove ordering.
				if (lastInput.Jump && mover.Grounded) {
					mover.Velocity.Y = jumpForce;
					mover.Grounded = false;
					mover.JumpCooldown = characterRes.JumpCooldownTime.To32();
				} else if (mover.Grounded) {
					mover.Velocity.Y = FP.Zero;
				}

				// Matches box3d's PreStep ordering: the cooldown a fresh jump above just set is
				// itself reduced by one tick immediately, same as every other tick.
				mover.JumpCooldown = FP.Max(FP.Zero, mover.JumpCooldown - dt);

				mover.Velocity = new FVector3(
					(moveInput.X * moveSpeed).To32(),
					mover.Velocity.Y + gravity.Y * dt,
					(moveInput.Y * moveSpeed).To32());

				var broadPhase = W.GetResource<BroadPhase>();
				var capsule = new Capsule(mover.CapsuleCenter1, mover.CapsuleCenter2, mover.CapsuleRadius);
				var moverXf = transform.ToWorldTransform();

				mover.Grounded = CharacterMover.UpdatePogoGrounding(
					broadPhase, moverXf, capsule, dt, characterRes.PogoHertz.To32(), characterRes.PogoDampingRatio.To32(), mover.JumpCooldown,
					characterRes.MaxSlopeNormalThreshold.To32(), ref mover.PogoVelocity);

				var target = moverXf.Position + dt * mover.Velocity + dt * mover.PogoVelocity * FVector3.Up;

				var planes = new MoverPlaneBuffer();
				for (var iteration = 0; iteration < 5; iteration++) {
					planes.Clear();
					CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);

					var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
					var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
					delta *= fraction;
					moverXf.Position += delta;

					if (FVector3.LengthSqr(delta) < Tolerance * Tolerance) {
						break;
					}
				}

				CharacterMover.ApplyPushImpulses(moverXf, mover.Velocity, in planes);

				mover.Velocity = MoverSolver.ClipVector(mover.Velocity, in planes);
				transform.SetFromWorldTransform(moverXf);
			});
		}
	}
}
