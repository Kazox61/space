using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

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
	/// resimulation, potentially dozens of times in one frame, so unlike most of this codebase's
	/// per-tick `List&lt;T&gt;`/array allocations, this file avoids allocating anything in its hot
	/// path: candidate lists and shape-proxy point arrays are static, reused scratch buffers
	/// (box3d's own b3ShapeProxy is just a pointer into shape-owned memory, never rebuilt per
	/// query -- this is the closest C# equivalent without changing Shape's array-free,
	/// rollback-serializable field layout).
	/// </summary>
	public static class CharacterMover {
		private static readonly List<EntityGID> CastCandidates = new();
		private static readonly List<EntityGID> CollideCandidates = new();

		private static readonly FVector3[] MoverCapsuleProxyPoints = new FVector3[2];
		private static readonly FVector3[] CandidateSphereProxyPoints = new FVector3[1];
		private static readonly FVector3[] CandidateCapsuleProxyPoints = new FVector3[2];
		private static readonly FVector3[] CandidateHullProxyPoints = new FVector3[8];
		private static readonly FVector3[] GroundProbeProxyPoints = new FVector3[8];
		private static readonly List<EntityGID> GroundProbeCandidates = new();

		/// <summary>
		/// Sweeps <paramref name="capsule"/> from <paramref name="moverXf"/> by
		/// <paramref name="translation"/> and returns the fraction of that translation safe to
		/// travel before hitting something, in [0, maxFraction]. Mirrors box3d's b3World_CastMover:
		/// only candidates that report a genuine hit at a positive fraction narrow the result -- a
		/// fraction-zero result (already touching/overlapping) is ignored, since
		/// <see cref="CollideMover"/>'s plane-solve is what resolves existing overlap, not the sweep.
		/// </summary>
		public static FP CastMover(BroadPhase broadPhase, FWorldTransform moverXf, Capsule capsule, FVector3 translation, FP maxFraction) {
			var localAabb = Capsule.ComputeSweptAABB(capsule, FTransform.Identity, new FTransform(translation, FQuaternion.Identity));
			var queryAabb = FWorldTransform.OffsetAABB(localAabb, moverXf.Position);

			MoverCapsuleProxyPoints[0] = capsule.Center1;
			MoverCapsuleProxyPoints[1] = capsule.Center2;
			var moverProxy = new ShapeProxy { Points = MoverCapsuleProxyPoints, Radius = capsule.Radius };

			CastCandidates.Clear();
			broadPhase.Query(queryAabb, CastCandidates);

			var bestFraction = maxFraction;
			for (var i = 0; i < CastCandidates.Count; i++) {
				if (!CastCandidates[i].TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor) {
					continue;
				}

				var pairInput = new ShapeCastPairInput {
					ProxyA = MakeCandidateProxy(shape),
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
			var localAabb = Capsule.ComputeAABB(capsule, FTransform.Identity);
			var margin = new FVector3(B3Config.SpeculativeDistance, B3Config.SpeculativeDistance, B3Config.SpeculativeDistance);
			localAabb = new FAABB(localAabb.LowerBound - margin, localAabb.UpperBound + margin);
			var queryAabb = FWorldTransform.OffsetAABB(localAabb, moverXf.Position);

			var moverShape = Shape.MakeCapsule(capsule.Center1, capsule.Center2, capsule.Radius);

			CollideCandidates.Clear();
			broadPhase.Query(queryAabb, CollideCandidates);

			for (var i = 0; i < CollideCandidates.Count; i++) {
				var candidateGid = CollideCandidates[i];
				if (!candidateGid.TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor) {
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
				if (body.Type != BodyType.Dynamic) {
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

				body.LinearVelocity += body.InvMass * impulse;
				body.AngularVelocity += body.InvInertiaWorld * FVector3.Cross(rB, impulse);
			}
		}

		/// <summary>
		/// Builds a <see cref="ShapeProxy"/> for a candidate shape into a reused static buffer sized
		/// exactly for its type (1/2/8 points for sphere/capsule/hull), instead of
		/// <see cref="Shape.MakeProxy"/>'s fresh-array-per-call. Kept local to this file rather than
		/// added to <see cref="Shape"/> itself, since <c>MakeProxy</c> is a general-purpose API other
		/// callers may reasonably expect to return an independent, non-aliased array.
		/// </summary>
		private static ShapeProxy MakeCandidateProxy(in Shape shape) {
			switch (shape.Type) {
				case ShapeType.Sphere:
					CandidateSphereProxyPoints[0] = shape.SphereShape.Center;
					return new ShapeProxy { Points = CandidateSphereProxyPoints, Radius = shape.SphereShape.Radius };

				case ShapeType.Capsule:
					CandidateCapsuleProxyPoints[0] = shape.CapsuleShape.Center1;
					CandidateCapsuleProxyPoints[1] = shape.CapsuleShape.Center2;
					return new ShapeProxy { Points = CandidateCapsuleProxyPoints, Radius = shape.CapsuleShape.Radius };

				case ShapeType.Hull:
					shape.HullShape.WriteCorners(CandidateHullProxyPoints);
					return new ShapeProxy { Points = CandidateHullProxyPoints, Radius = FP.Zero };

				default:
					return new ShapeProxy { Points = System.Array.Empty<FVector3>(), Radius = FP.Zero };
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
		private static GroundTraceResult TraceBody(BroadPhase broadPhase, FPos origin, FVector3 translation, FP halfWidth, FP halfDepth, FP halfHeight) {
			for (var i = 0; i < 8; i++) {
				var sx = (i & 1) != 0 ? halfWidth : -halfWidth;
				var sy = (i & 2) != 0 ? halfHeight : -halfHeight;
				var sz = (i & 4) != 0 ? halfDepth : -halfDepth;
				GroundProbeProxyPoints[i] = new FVector3(sx, sy, sz);
			}

			var proxy = new ShapeProxy { Points = GroundProbeProxyPoints, Radius = FP.Zero };
			var probeXf = new FWorldTransform(origin, FQuaternion.Identity);

			var treeOrigin = new FVector3(origin.X.To32(), origin.Y.To32(), origin.Z.To32());
			var localExtent = new FVector3(halfWidth, halfHeight, halfDepth);
			var sweptMin = FVector3.MinComponents(-localExtent, -localExtent + translation);
			var sweptMax = FVector3.MaxComponents(localExtent, localExtent + translation);
			var queryAabb = new FAABB(treeOrigin + sweptMin, treeOrigin + sweptMax);

			GroundProbeCandidates.Clear();
			broadPhase.Query(queryAabb, GroundProbeCandidates);

			var result = new GroundTraceResult();
			var bestFraction = FP.One;

			for (var i = 0; i < GroundProbeCandidates.Count; i++) {
				if (!GroundProbeCandidates[i].TryUnpack<TWorld>(out var shapeEntity) || !TryGetShapeAndTransform(shapeEntity, out var shape, out var candidateXf)) {
					continue;
				}

				if (shape.IsSensor) {
					continue;
				}

				var pairInput = new ShapeCastPairInput {
					ProxyA = MakeCandidateProxy(shape),
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
			var trace = TraceBody(broadPhase, origin, translation, halfWidth, halfWidth, halfHeight);

			while (trace.StartedSolid || (trace.Hit && !IsStandableSurface(trace.Normal, maxSlopeNormalThreshold))) {
				radiusScale -= FP.FromRatio(1, 10);
				if (radiusScale < FP.FromRatio(7, 10)) {
					pogoVelocity = FP.Zero;
					return false;
				}

				halfWidth = capsule.Radius * FP.Half * radiusScale;
				trace = TraceBody(broadPhase, origin, translation, halfWidth, halfWidth, halfHeight);
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
	}
}
