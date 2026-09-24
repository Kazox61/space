using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// World-level orchestration for box3d's Character Mover API (docs/character.md, src/mover.c):
	/// swept capsule casting (<see cref="CastMover"/>) and fixed-position contact-plane gathering
	/// (<see cref="CollideMover"/>) against everything currently in the broad phase. The mover
	/// itself is never a Body/Shape -- it's a free capsule driven by application code each tick,
	/// exactly as box3d's docs describe ("exists outside the rigid body simulation").
	///
	/// Called up to 10x/tick (2 queries x up to 5 SolveMove iterations) and, during rollback
	/// resimulation, potentially dozens of times in one frame, so the hot path allocates nothing:
	/// shape proxies store their points inline, and candidate lists are rented from the world's
	/// <see cref="PhysicsRuntime"/> rather than held in static fields, so concurrent worlds and nested
	/// queries never share scratch state.
	/// </summary>
	public static class CharacterMover {

		/// <summary>
		/// Sweeps <paramref name="capsule"/> from <paramref name="moverXf"/> by
		/// <paramref name="translation"/> and returns the fraction of that translation safe to
		/// travel before hitting something, in [0, maxFraction]. Mirrors box3d's b3World_CastMover:
		/// only candidates that report a genuine hit at a positive fraction narrow the result -- a
		/// fraction-zero result (already touching/overlapping) is ignored, since
		/// <see cref="CollideMover"/>'s plane-solve is what resolves existing overlap, not the sweep.
		/// </summary>
		public static FP CastMover(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, FVector3 translation, FP maxFraction) {
			return CastMover(broadPhase, moverXf, capsule, translation, maxFraction, Filter.Default);
		}

		public static FP CastMover(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, FVector3 translation, FP maxFraction, Filter filter) {
			PhysicsValidation.ValidateCapsuleQuery(moverXf, capsule, translation);
			if (maxFraction < FP.Zero || maxFraction > FP.One)
				throw new ArgumentOutOfRangeException(nameof(maxFraction), "Mover cast fraction must be in [0, 1].");
			var localAabb = Capsule.ComputeSweptAABB(capsule, FTransform.Identity, new FTransform(translation, FQuaternion.Identity));
			var queryAabb = FWorldTransform.OffsetAABB(localAabb, moverXf.Position);

			var moverProxy = ShapeProxy.MakeSegment(capsule.Center1, capsule.Center2, capsule.Radius);

			var runtime = PhysicsRuntime.Get();
			var candidates = runtime.RentGidList();
			broadPhase.Query(queryAabb, QueryMask(filter), candidates);
			candidates.Sort(CompareGid);

			var bestFraction = maxFraction;
			for (var i = 0; i < candidates.Count; i++) {
				if (!candidates[i].TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor || !Filter.ShouldCollide(filter, shape.Filter)) {
					continue;
				}

				var pairInput = new ShapeCastPairInput {
					ProxyA = shape.MakeProxy(),
					ProxyB = moverProxy,
					Transform = FWorldTransform.InvMul(candidateXf, moverXf),
					TranslationB = FQuaternion.Inverse(candidateXf.Rotation) * translation,
					MaxFraction = bestFraction,
					CanEncroach = true,
				};

				var output = Distance.ShapeCast(pairInput);
				if (output.Hit && output.Fraction > FP.Zero && output.Fraction < bestFraction) {
					bestFraction = output.Fraction;
				}
			}

			runtime.Return(candidates);
			return bestFraction;
		}

		/// <summary>
		/// Gathers contact planes for <paramref name="capsule"/> at its current (fixed) position
		/// <paramref name="moverXf"/>, appending to <paramref name="outPlanes"/>. Mirrors box3d's
		/// b3World_CollideMover, but reuses <see cref="Manifold.Collide"/> generically across shape
		/// types instead of box3d's per-type b3CollideMoverAndSphere/Hull dispatch -- notably, unlike
		/// box3d's hull case (which deliberately produces no plane on deep overlap), Manifold.Collide
		/// stays well-defined under deep overlap, so this is strictly more robust, not a regression.
		/// </summary>
		public static void CollideMover(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, ref MoverPlaneBuffer outPlanes) {
			CollideMover(broadPhase, moverXf, capsule, Filter.Default, ref outPlanes);
		}

		public static void CollideMover(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, Filter filter, ref MoverPlaneBuffer outPlanes) {
			PhysicsValidation.ValidateCapsuleQuery(moverXf, capsule, FVector3.Zero);
			var localAabb = Capsule.ComputeAABB(capsule, FTransform.Identity);
			var margin = new FVector3(B3Config.SpeculativeDistance, B3Config.SpeculativeDistance, B3Config.SpeculativeDistance);
			localAabb = new FAABB(localAabb.LowerBound - margin, localAabb.UpperBound + margin);
			var queryAabb = FWorldTransform.OffsetAABB(localAabb, moverXf.Position);

			var moverShape = Shape.MakeCapsule(capsule.Center1, capsule.Center2, capsule.Radius);

			var runtime = PhysicsRuntime.Get();
			var candidates = runtime.RentGidList();
			broadPhase.Query(queryAabb, QueryMask(filter), candidates);
			candidates.Sort(CompareGid);

			for (var i = 0; i < candidates.Count; i++) {
				var candidateGid = candidates[i];
				if (!candidateGid.TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor || !Filter.ShouldCollide(filter, shape.Filter)) {
					continue;
				}

				var manifold = Manifold.Collide(moverShape, moverXf, shape, candidateXf);
				if (manifold.PointCount == 0 || manifold.MinSeparation() > B3Config.SpeculativeDistance) {
					continue;
				}

				for (var j = 0; j < manifold.PointCount; j++) {
					var point = manifold.GetPoint(j);

					// Manifold.Collide's normal points mover(A) -> candidate(B); a mover plane needs
					// the opposite (escape/outward) direction. BaseSeparation stays as-is -- it's a
					// signed scalar gap, unaffected by which way the normal happens to point.
					outPlanes.Add(new MoverPlane {
						Normal = -manifold.Normal,
						BaseSeparation = point.Separation,
						Point = point.Point,
						ShapeGid = candidateGid,
						PushLimit = FP.MaxValue,
						Push = FP.Zero,
						ClipVelocity = true,
					});
				}
			}

			runtime.Return(candidates);
		}

		/// <summary>
		/// One-sided velocity-only impulse for every plane whose shape belongs to a dynamic body,
		/// treating the mover as infinite mass (never itself loses velocity). Ported from box3d's
		/// CharacterMover::SolveMove post-loop block (samples/sample.cpp) -- the exact math
		/// <see cref="ContactSolverSystem.ApplyImpulse"/> already uses for its B-body half.
		/// </summary>
		public static void ApplyPushImpulses(FWorldTransform moverXf, FVector3 moverVelocity, in MoverPlaneBuffer planes) {
			for (var i = 0; i < planes.Count; i++) {
				var plane = planes.GetPlane(i);

				if (!plane.ShapeGid.TryUnpack<TWorld>(out var shapeEntity) || !shapeEntity.Has<W.Link<BodyOwner>>()) {
					continue;
				}

				ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
				if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
					continue;
				}

				ref var body = ref bodyEntity.Ref<Body>()!;
				if (body.Type != BodyType.Dynamic || !BodyOperations.IsEnabled(body)) {
					continue;
				}

				var normal = -plane.Normal;
				var worldPoint = FWorldTransform.TransformPoint(moverXf, plane.Point);
				var rB = worldPoint - body.Center;

				var rnB = FVector3.Cross(rB, normal);
				var kNormal = body.InvMass + FVector3.Dot(rnB, body.InvInertiaWorld * rnB);
				var normalMass = kNormal > FP.Zero ? FP.One / kNormal : FP.Zero;

				var vrB = body.LinearVelocity + FVector3.Cross(body.AngularVelocity, rB);
				var vn = FVector3.Dot(vrB - moverVelocity, normal);
				var impulse = FP.Max(-normalMass * vn, FP.Zero) * normal;

				if (impulse == FVector3.Zero) {
					continue;
				}

				body.LinearVelocity += body.InvMass * impulse;
				body.AngularVelocity += body.InvInertiaWorld * FVector3.Cross(rB, impulse);
				PhysicsSleep.WakeBody(ref body);
			}
		}

		/// <summary>Result of <see cref="TraceBody"/> -- box3d's TraceResult (samples/sample_character.cpp), trimmed to the fields <see cref="UpdatePogoGrounding"/> needs.</summary>
		private struct GroundTraceResult {
			public bool Hit;
			public bool StartedSolid;
			public FP Fraction;
			public FVector3 Normal;
		}

		/// <summary>
		/// Sweeps a small axis-aligned box (never rotated with the mover -- box3d's own probe is
		/// world-space-only too) from <paramref name="origin"/> by <paramref name="translation"/>,
		/// footprint <paramref name="halfWidth"/>/<paramref name="halfDepth"/> wide,
		/// <paramref name="halfHeight"/> tall, centered on <paramref name="origin"/>. Ported from
		/// box3d's TraceBody (samples/sample_character.cpp) -- structurally the same candidate-sweep
		/// loop as <see cref="CastMover"/>, just against a box proxy built fresh here instead of the
		/// mover's own capsule, and reporting hit/normal/deep-overlap instead of only a fraction.
		/// </summary>
		private static GroundTraceResult TraceBody(BroadPhase broadPhase, FPos origin, FVector3 translation, FP halfWidth, FP halfDepth, FP halfHeight, Filter filter) {
			var proxy = new ShapeProxy { Count = 8, Radius = FP.Zero };
			for (var i = 0; i < 8; i++) {
				var sx = (i & 1) != 0 ? halfWidth : -halfWidth;
				var sy = (i & 2) != 0 ? halfHeight : -halfHeight;
				var sz = (i & 4) != 0 ? halfDepth : -halfDepth;
				proxy.Points[i] = new FVector3(sx, sy, sz);
			}

			var probeXf = new FWorldTransform(origin, FQuaternion.Identity);

			var treeOrigin = new FVector3(origin.X.To32(), origin.Y.To32(), origin.Z.To32());
			var localExtent = new FVector3(halfWidth, halfHeight, halfDepth);
			var sweptMin = FVector3.MinComponents(-localExtent, -localExtent + translation);
			var sweptMax = FVector3.MaxComponents(localExtent, localExtent + translation);
			var queryAabb = new FAABB(treeOrigin + sweptMin, treeOrigin + sweptMax);

			var runtime = PhysicsRuntime.Get();
			var candidates = runtime.RentGidList();
			broadPhase.Query(queryAabb, QueryMask(filter), candidates);
			candidates.Sort(CompareGid);

			var result = new GroundTraceResult();
			var bestFraction = FP.One;

			for (var i = 0; i < candidates.Count; i++) {
				if (!candidates[i].TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor || !Filter.ShouldCollide(filter, shape.Filter)) {
					continue;
				}

				var pairInput = new ShapeCastPairInput {
					ProxyA = shape.MakeProxy(),
					ProxyB = proxy,
					Transform = FWorldTransform.InvMul(candidateXf, probeXf),
					TranslationB = FQuaternion.Inverse(candidateXf.Rotation) * translation,
					MaxFraction = bestFraction,
					CanEncroach = true,
				};

				var output = Distance.ShapeCast(pairInput);
				if (!output.Hit) {
					continue;
				}

				if (output.Fraction == FP.Zero) {
					// Deep overlap right at the sweep start -- box3d's "startedSolid": this box is
					// too wide for the gap it's in, not a useful ground reading. Doesn't affect
					// bestFraction/other candidates; the caller's radius-shrink retry handles it.
					result.StartedSolid = true;
					continue;
				}

				if (output.Fraction < bestFraction) {
					bestFraction = output.Fraction;
					result.Hit = true;
					result.Fraction = output.Fraction;
					result.Normal = candidateXf.Rotation * output.Normal;
				}
			}

			runtime.Return(candidates);
			return result;
		}

		/// <summary>Ported from box3d's IsStandableSurface (samples/sample_character.cpp): is this hit normal upward-facing enough to stand on?</summary>
		private static bool IsStandableSurface(FVector3 normal, FP maxSlopeNormalThreshold) {
			return normal.Y >= maxSlopeNormalThreshold;
		}

		/// <summary>
		/// box3d's ground check, ported from box3d's more complete character sample's CategorizeGround
		/// (samples/sample_character.cpp), not the plain single-ray version in the simpler CharacterMover
		/// sample (samples/sample.cpp) this project otherwise ports its movement loop from: a single
		/// ray either hits or it doesn't, so a mover standing mostly off a ledge with its ray still
		/// grazing the corner reads as fully grounded -- observed in practice as the mover "sticking"
		/// to edges instead of falling off them. CategorizeGround's fix is a small box sweep instead
		/// (<see cref="TraceBody"/>), shrunk and retried if it starts solid or lands on a non-standable
		/// slope, giving up below 70% width -- ported verbatim (same 0.1 shrink step, same 0.7 floor).
		///
		/// The resulting fraction still drives the same critically-damped spring-damper toward
		/// <c>pogoRestLength</c> (see the removed single-ray version's remarks on why that's
		/// <c>capsule.Radius</c>, not box3d's literal <c>3*radius</c>) -- CategorizeGround itself has
		/// no spring (box3d's more complete sample instead hard-snaps position via a separate Reground
		/// step, driving an actual dynamic rigid body through box3d's own contact solver, not the
		/// CollideMover/SolvePlanes/CastMover kinematic sweep this project's mover uses). Porting that
		/// whole architecture is out of scope for a ground-check fix; only CategorizeGround/TraceBody's
		/// footprint-aware sweep is ported here, feeding the same spring this project already has.
		/// </summary>
		public static bool UpdatePogoGrounding(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, FP dt, FP hertz, FP dampingRatio, FP jumpCooldown, FP maxSlopeNormalThreshold, ref FP pogoVelocity) {
			return UpdatePogoGrounding(broadPhase, moverXf, capsule, dt, hertz, dampingRatio, jumpCooldown, maxSlopeNormalThreshold, Filter.Default, ref pogoVelocity);
		}

		public static bool UpdatePogoGrounding(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, FP dt, FP hertz, FP dampingRatio, FP jumpCooldown, FP maxSlopeNormalThreshold, Filter filter, ref FP pogoVelocity) {
			PhysicsValidation.ValidateCapsuleQuery(moverXf, capsule, FVector3.Zero);
			// See Mover.JumpCooldown's remarks: skip the trace entirely while a jump is still in its
			// cooldown window, matching box3d's CategorizeGround gating re-grounding on m_jumpCooldown.
			if (jumpCooldown > FP.Zero) {
				pogoVelocity = FP.Zero;
				return false;
			}

			var pogoRestLength = capsule.Radius;
			var rayLength = pogoRestLength + capsule.Radius;
			var origin = FWorldTransform.TransformPoint(moverXf, capsule.Center1);
			var translation = new FVector3(FP.Zero, -rayLength, FP.Zero);

			var halfHeight = capsule.Radius * FP.Half;
			var radiusScale = FP.One;
			var halfWidth = capsule.Radius * FP.Half * radiusScale;
			var trace = TraceBody(broadPhase, origin, translation, halfWidth, halfWidth, halfHeight, filter);

			while (trace.StartedSolid || (trace.Hit && !IsStandableSurface(trace.Normal, maxSlopeNormalThreshold))) {
				radiusScale -= FP.FromRatio(1, 10);
				if (radiusScale < FP.FromRatio(7, 10)) {
					pogoVelocity = FP.Zero;
					return false;
				}

				halfWidth = capsule.Radius * FP.Half * radiusScale;
				trace = TraceBody(broadPhase, origin, translation, halfWidth, halfWidth, halfHeight, filter);
			}

			if (trace.StartedSolid || !trace.Hit || !IsStandableSurface(trace.Normal, maxSlopeNormalThreshold)) {
				pogoVelocity = FP.Zero;
				return false;
			}

			// trace.Fraction*rayLength is how far the box's CENTER travelled before its BOTTOM face
			// (halfHeight closer to the ground than the center) touched down -- the box's own
			// thickness lets it "reach" the ground at a smaller fraction than a zero-size probe
			// would have needed. The spring wants the zero-size-probe distance (Center1 to ground),
			// which is therefore this fraction's distance *plus* halfHeight, not the raw fraction
			// alone -- getting this backwards once already settled the mover halfHeight too high.
			var pogoCurrentLength = trace.Fraction * rayLength + halfHeight;

			var omega = 2 * FP.Pi * hertz;
			var omegaH = omega * dt;
			pogoVelocity = (pogoVelocity - omega * omegaH * (pogoCurrentLength - pogoRestLength))
				/ (FP.One + 2 * dampingRatio * omegaH + omegaH * omegaH);

			return true;
		}

		private static bool TryGetShapeAndTransform(W.Entity shapeEntity, out Shape shape, out FWorldTransform transform) {
			ref readonly var shapeRef = ref shapeEntity.Read<Shape>()!; // Broad-phase proxies are always shape entities.
			shape = shapeRef;
			transform = default;

			if (!shapeEntity.Has<W.Link<BodyOwner>>()) {
				return false;
			}

			ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
			if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
				return false;
			}

			transform = bodyEntity.Read<Body>()!.Transform; // BodyOwner always links to an entity with Body.
			return true;
		}

		private static readonly Comparison<EntityGID> CompareGid = static (a, b) => a.Raw.CompareTo(b.Raw);

		private static ulong QueryMask(Filter filter) => filter.GroupIndex > 0 ? ulong.MaxValue : filter.MaskBits;
	}
}
