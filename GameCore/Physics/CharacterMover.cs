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
