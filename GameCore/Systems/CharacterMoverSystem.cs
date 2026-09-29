using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Executes every character's <see cref="CharacterMoveIntent"/> (written by
	/// <see cref="PlayerIntentSystem"/> or <see cref="NavAgentSystem"/>) via box3d's Character Mover algorithm
	/// (<see cref="CharacterMover"/>/<see cref="MoverSolver"/>): collide -> solve -> cast -> move,
	/// repeated up to 5 times per tick to converge, then a one-sided push impulse onto any dynamic
	/// body the mover displaced. Writes <see cref="Transform"/> directly -- characters have no
	/// <see cref="Body"/>, so unlike Body-owning entities there's no BodyTransformSyncSystem involved
	/// here; this is the sole writer of a character's Transform.
	///
	/// Ground detection is box3d's footprint-aware ground check
	/// (<see cref="CharacterMover.UpdatePogoGrounding"/>, ported from the more complete character
	/// sample's CategorizeGround/TraceBody -- a small box sweep, not a single ray, so standing
	/// mostly off a ledge doesn't still read as grounded), computed fresh each tick from
	/// <see cref="Mover.CapsuleCenter1"/>, before the movement target is built; its spring-damper
	/// output (<see cref="Mover.PogoVelocity"/>) and the supporting body's point velocity are added
	/// into that target alongside gravity-integrated <see cref="Mover.Velocity"/>. Support velocity
	/// is retained on takeoff so rollback-corrected jumps from moving platforms keep their momentum.
	/// Rail proxies cover both poses and kinematic collision uses the predicted pose for this tick,
	/// keeping vertical riders relative to the support that physics integrates later in the pipeline.
	/// </summary>
	public struct CharacterMoverSystem : ISystem {
		private static readonly FP Tolerance = FP.FromRatio(1, 100);

		public void Update() {
			var broadPhase = W.GetResource<BroadPhase>();
			var dt = Const.DeltaTime.To32();
			foreach (var platform in W.Query<All<PatrolRail, Body>>().Entities()) {
				ref readonly var body = ref platform.Read<Body>();
				ShapeProxySystem.RefreshBodyAABBs(platform, body, broadPhase, dt * body.LinearVelocity);
			}

			W.Query().For(static (ref Transform transform, ref Mover mover, ref CharacterMoveIntent intent) => {
				// Past the physics escape line the mover's queries would leave the supported envelope, so the
				// character stops simulating in place (the counterpart of OutOfPhysicsBounds for bodies).
				if (!PhysicsValidation.IsInsideSimulationBounds(transform.ToWorldTransform().Position)) {
					mover.Velocity = FVector3.Zero;
					mover.PogoVelocity = FP.Zero;
					mover.Grounded = false;
					mover.SupportVelocity = FVector3.Zero;
					mover.InheritedVelocity = FVector3.Zero;
					return;
				}

				var characterRes = Systems.GetResource<CharacterRes>();
				var jumpForce = characterRes.JumpForce.To32();
				var gravity = -characterRes.Gravity.To32();
				var dt = Const.DeltaTime.To32();

				// Jump check comes before the grounded reset (and clears Grounded immediately) so the
				// fresh jump velocity isn't stomped back to zero by that same reset this tick --
				// mirrors box3d's CharacterMover::Step/SolveMove ordering.
				var wasGrounded = mover.Grounded;
				var previousSupportVelocity = mover.SupportVelocity;
				if (intent.Jump && mover.Grounded) {
					mover.Velocity.Y = jumpForce;
					mover.Grounded = false;
					mover.JumpCooldown = characterRes.JumpCooldownTime.To32();
				} else if (mover.Grounded) {
					mover.Velocity.Y = FP.Zero;
				}

				// Matches box3d's PreStep ordering: the cooldown a fresh jump above just set is
				// itself reduced by one tick immediately, same as every other tick.
				mover.JumpCooldown = FP.Max(FP.Zero, mover.JumpCooldown - dt);

				// TODO: fall speed is uncapped. After ~680 units of free fall (e.g. walking off the ground)
				// Velocity.Y passes ~181 u/s, where squared lengths overflow Q16.16 and the mover misbehaves.
				// If that matters, clamp Velocity.Y to a terminal speed (e.g. a MaxFallSpeed of ~40-50 in
				// CharacterRes), in line with the 60 u/s cap bodies get. See docs/physics-limits.md.
				mover.Velocity = new FVector3(
					intent.Velocity.X.To32(),
					mover.Velocity.Y + gravity * dt,
					intent.Velocity.Y.To32());

				var broadPhase = W.GetResource<BroadPhase>();
				var capsule = new Capsule(mover.CapsuleCenter1, mover.CapsuleCenter2, mover.CapsuleRadius);
				var moverXf = transform.ToWorldTransform();
				var moverFilter = Filter.Default;
				moverFilter.GroupIndex = mover.FilterGroup;

				mover.Grounded = CharacterMover.UpdatePogoGrounding(
					broadPhase, moverXf, capsule, dt, characterRes.PogoHertz.To32(), characterRes.PogoDampingRatio.To32(), mover.JumpCooldown,
					characterRes.MaxSlopeNormalThreshold.To32(), moverFilter, ref mover.PogoVelocity, out var supportVelocity, dt);

				if (mover.Grounded) {
					mover.SupportVelocity = supportVelocity;
					mover.InheritedVelocity = FVector3.Zero;
				} else {
					if (wasGrounded) {
						mover.InheritedVelocity = previousSupportVelocity;
					}
					mover.SupportVelocity = FVector3.Zero;
				}

				var carriedVelocity = mover.Grounded ? mover.SupportVelocity : mover.InheritedVelocity;
				var effectiveVelocity = mover.Velocity + carriedVelocity;
				var target = moverXf.Position + dt * effectiveVelocity + dt * mover.PogoVelocity * FVector3.Up;

				var planes = new MoverPlaneBuffer();
				for (var iteration = 0; iteration < 5; iteration++) {
					planes.Clear();
					CharacterMover.CollideMover(broadPhase, moverXf, capsule, moverFilter, dt, ref planes);

					var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
					var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One, moverFilter, dt);
					delta *= fraction;
					moverXf.Position += delta;

					if (FVector3.LengthSqr(delta) < Tolerance * Tolerance) {
						break;
					}
				}

				CharacterMover.ApplyPushImpulses(moverXf, effectiveVelocity, in planes);

				var clippedVelocity = MoverSolver.ClipVector(mover.Velocity, in planes);
				if (!mover.Grounded) {
					mover.InheritedVelocity = MoverSolver.ClipVector(effectiveVelocity, in planes) - clippedVelocity;
				}
				mover.Velocity = clippedVelocity;
				transform.SetFromWorldTransform(moverXf);
			});
		}
	}
}
