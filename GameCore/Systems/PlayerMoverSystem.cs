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
	/// Ground detection is derived from <see cref="CharacterMover.CollideMover"/>'s own planes
	/// (any plane facing sufficiently upward) rather than box3d's separate downward raycast
	/// "pogo stick" -- this project has no world-level raycast API yet, and a plane-derived check
	/// is simpler while still working for flat ground and the tops of boxes. It carries a one-tick
	/// lag (this tick's grounded state was computed from last tick's final planes), which is
	/// imperceptible at 60Hz and matches how <see cref="Mover.Grounded"/> is documented.
	/// </summary>
	public struct PlayerMoverSystem : ISystem {
		private static readonly FP Tolerance = FP.FromRatio(1, 100);

		public void Update() {
			W.Query().For(static (ref PlayerInfo playerInfo, ref Transform transform, ref Mover mover) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				var lastInput = input.LastFresh();
				var moveInput = Fixed64.FVector2.NormalizeSafe(new Fixed64.FVector2(lastInput.MoveX, lastInput.MoveY));
				var moveSpeed = Systems.GetResource<CharacterRes>().MoveSpeed;
				var jumpForce = Systems.GetResource<CharacterRes>().JumpForce.To32();
				var gravity = W.GetResource<PhysicsWorld>().Gravity;
				var dt = Const.DeltaTime.To32();

				// Jump check comes before the grounded reset (and clears Grounded immediately) so the
				// fresh jump velocity isn't stomped back to zero by that same reset this tick --
				// mirrors box3d's CharacterMover::Step/SolveMove ordering.
				if (lastInput.Jump && mover.Grounded) {
					mover.Velocity.Y = jumpForce;
					mover.Grounded = false;
				} else if (mover.Grounded) {
					mover.Velocity.Y = FP.Zero;
				}

				mover.Velocity = new FVector3(
					(moveInput.X * moveSpeed).To32(),
					mover.Velocity.Y + gravity.Y * dt,
					(moveInput.Y * moveSpeed).To32());

				var broadPhase = W.GetResource<BroadPhase>();
				var capsule = new Capsule(mover.CapsuleCenter1, mover.CapsuleCenter2, mover.CapsuleRadius);
				var moverXf = transform.ToWorldTransform();
				var target = moverXf.Position + dt * mover.Velocity;

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
				mover.Grounded = MoverSolver.IsGrounded(in planes);
				transform.SetFromWorldTransform(moverXf);
			});
		}
	}
}
