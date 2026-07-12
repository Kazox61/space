using System;
using System.Diagnostics;
using Fixed32;
using Fixed;

namespace Space.GameCore;

/// <summary>A single contact point, in shape A's frame (matches <see cref="Manifold"/>'s Normal frame).</summary>
public struct ManifoldPoint {
	public FVector3 Point;
	public FP Separation;

	// Normal-impulse warm-start state. Friction is manifold-level (see Contact.FrictionImpulseX/Y),
	// not per-point — with a single point per manifold, the "centroid" friction basis is this point.
	public FP NormalImpulse;
}

/// <summary>
/// A contact manifold between two shapes. Sphere/sphere, sphere/capsule, and capsule/capsule stay
/// single-point (box3d's own b3FeaturePair_single path for round shapes); box3d's capsule/capsule
/// *does* add a second point when the capsules are nearly parallel (for resting stability), which
/// is deferred here (see <see cref="Collide"/>). Hull pairs (hull/sphere, hull/capsule, hull/hull)
/// need up to 4 points — a box resting flat needs box3d's reference-face-vs-incident-feature clip
/// to stay stable — so this manifold carries up to <see cref="MaxPoints"/> points, matching box3d's
/// B3_MAX_MANIFOLD_POINTS.
/// </summary>
public struct Manifold {
	public const int MaxPoints = 4;

	public FVector3 Normal;
	public int PointCount;
	public ManifoldPoint Point0;
	public ManifoldPoint Point1;
	public ManifoldPoint Point2;
	public ManifoldPoint Point3;

	public ManifoldPoint GetPoint(int index) {
		return index switch {
			0 => Point0,
			1 => Point1,
			2 => Point2,
			_ => Point3,
		};
	}

	public void SetPoint(int index, ManifoldPoint point) {
		switch (index) {
			case 0: Point0 = point; break;
			case 1: Point1 = point; break;
			case 2: Point2 = point; break;
			default: Point3 = point; break;
		}
	}

	/// <summary>The shallowest (least negative) separation across all points, or <see cref="FP.MaxValue"/> if there are none.</summary>
	public FP MinSeparation() {
		if (PointCount == 0) {
			return FP.MaxValue;
		}

		var min = Point0.Separation;
		if (PointCount > 1) min = FP.Min(min, Point1.Separation);
		if (PointCount > 2) min = FP.Min(min, Point2.Separation);
		if (PointCount > 3) min = FP.Min(min, Point3.Separation);
		return min;
	}

	/// <summary>
	/// Computes the manifold between two shapes given their owning bodies' world transforms.
	/// Analytic closest-point formulas ported from box3d's convex_manifold.c
	/// (b3CollideSpheres / b3CollideCapsuleAndSphere / b3CollideCapsules) — unlike a raw GJK
	/// distance query, these report a correctly *signed* separation (negative when overlapping)
	/// and a normal that stays stable and well-defined under deep overlap, which the contact
	/// solver's speculative-margin/bias math depends on. Everything is computed directly in shape
	/// A's frame, so no result needs flipping regardless of which shape (sphere/capsule) is A or B.
	/// Deferred: box3d's near-parallel capsule/capsule two-point clipping (stability refinement,
	/// not a correctness requirement — a single point at the deepest overlap still holds).
	/// </summary>
	public static Manifold Collide(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB) {
		var xfBinA = FWorldTransform.InvMul(xfA, xfB);

		return (a.Type, b.Type) switch {
			(ShapeType.Sphere, ShapeType.Sphere) => CollideSphereSphere(a.SphereShape, b.SphereShape, xfBinA),
			(ShapeType.Sphere, ShapeType.Capsule) => CollideSphereCapsule(a.SphereShape, b.CapsuleShape, xfBinA),
			(ShapeType.Capsule, ShapeType.Sphere) => CollideCapsuleSphere(a.CapsuleShape, b.SphereShape, xfBinA),
			(ShapeType.Capsule, ShapeType.Capsule) => CollideCapsuleCapsule(a.CapsuleShape, b.CapsuleShape, xfBinA),
			(ShapeType.Hull, ShapeType.Sphere) => CollideHullSphere(a.HullShape, b.SphereShape, xfBinA),
			(ShapeType.Sphere, ShapeType.Hull) => CollideSphereHull(a.SphereShape, b.HullShape, xfBinA),
			(ShapeType.Hull, ShapeType.Capsule) => CollideHullCapsule(a.HullShape, b.CapsuleShape, xfBinA),
			(ShapeType.Capsule, ShapeType.Hull) => CollideCapsuleHull(a.CapsuleShape, b.HullShape, xfBinA),
			(ShapeType.Hull, ShapeType.Hull) => CollideHullHull(a.HullShape, b.HullShape, xfBinA),
			_ => UnsupportedPair(a.Type, b.Type),
		};
	}

	/// <summary>
	/// Reached for any shape-type combination this port doesn't implement yet (mesh/height field/
	/// compound, on either side). Returns an empty manifold -- these two shapes will never touch,
	/// silently, exactly like every other unsupported pair here, but at least surfaces the
	/// misconfiguration in DEBUG builds instead of looking identical to two shapes that are simply
	/// far apart.
	/// </summary>
	private static Manifold UnsupportedPair(ShapeType a, ShapeType b) {
		Debug.Assert(false, $"Manifold.Collide: no narrow-phase pair for ({a}, {b}) -- these two shapes will never generate a contact.");
		return default;
	}

	/// <summary>Builds a single-point manifold from a closest-point pair and radii, all in A's frame.</summary>
	private static Manifold FromClosestPoints(FVector3 pointOnA, FVector3 pointOnB, FP radiusA, FP radiusB) {
		var offset = pointOnB - pointOnA;
		var distanceSqr = FVector3.LengthSqr(offset);

		// Default direction (matches box3d) when the two closest points coincide — division by a
		// near-zero length would otherwise produce an unstable/undefined normal.
		var normal = new FVector3(FP.Zero, FP.One, FP.Zero);
		if (distanceSqr > FP.CalculationsEpsilonSqr) {
			normal = offset / FP.Sqrt(distanceSqr);
		}

		var distance = FP.Sqrt(distanceSqr);
		var point = FP.Half * ((pointOnA + radiusA * normal) + (pointOnB - radiusB * normal));

		return new Manifold {
			Normal = normal,
			PointCount = 1,
			Point0 = new ManifoldPoint {
				Point = point,
				Separation = distance - (radiusA + radiusB),
			},
		};
	}

	private static Manifold CollideSphereSphere(Sphere a, Sphere b, FTransform xfBinA) {
		var centerA = a.Center;
		var centerB = FTransform.TransformPoint(xfBinA, b.Center);
		return FromClosestPoints(centerA, centerB, a.Radius, b.Radius);
	}

	private static Manifold CollideSphereCapsule(Sphere a, Capsule b, FTransform xfBinA) {
		var centerA = a.Center;
		var c1 = FTransform.TransformPoint(xfBinA, b.Center1);
		var c2 = FTransform.TransformPoint(xfBinA, b.Center2);
		var closestOnB = PointToSegmentClosestPoint(c1, c2, centerA);
		return FromClosestPoints(centerA, closestOnB, a.Radius, b.Radius);
	}

	private static Manifold CollideCapsuleSphere(Capsule a, Sphere b, FTransform xfBinA) {
		var centerB = FTransform.TransformPoint(xfBinA, b.Center);
		var closestOnA = PointToSegmentClosestPoint(a.Center1, a.Center2, centerB);
		return FromClosestPoints(closestOnA, centerB, a.Radius, b.Radius);
	}

	private static Manifold CollideCapsuleCapsule(Capsule a, Capsule b, FTransform xfBinA) {
		var b1 = FTransform.TransformPoint(xfBinA, b.Center1);
		var b2 = FTransform.TransformPoint(xfBinA, b.Center2);
		var (closestOnA, closestOnB) = SegmentSegmentClosestPoints(a.Center1, a.Center2, b1, b2);
		return FromClosestPoints(closestOnA, closestOnB, a.Radius, b.Radius);
	}

	private static Manifold CollideHullSphere(Hull a, Sphere b, FTransform xfBinA) {
		var centerB = FTransform.TransformPoint(xfBinA, b.Center);
		return CollideBoxSphere(a.Center, a.Rotation, a.HalfExtents, centerB, b.Radius);
	}

	private static Manifold CollideSphereHull(Sphere a, Hull b, FTransform xfBinA) {
		var boxCenter = FTransform.TransformPoint(xfBinA, b.Center);
		var boxRotation = xfBinA.Rotation * b.Rotation;
		var manifold = CollideBoxSphere(boxCenter, boxRotation, b.HalfExtents, a.Center, a.Radius);
		// CollideBoxSphere's normal points from the box toward the sphere. Here the box is the pair's
		// B shape and the sphere is A, so flip to keep this file's A-to-B normal convention.
		manifold.Normal = -manifold.Normal;
		return manifold;
	}

	/// <summary>
	/// Box (zero-radius hull) versus sphere, all arguments already expressed in a common frame.
	/// Ported from box3d's convex-manifold box/sphere path: clamp the sphere center into the box's
	/// own local axes to find the closest surface point; if the center is already inside, push out
	/// along the axis of least penetration instead (SAT for a point). The normal points from the box
	/// toward the sphere.
	/// </summary>
	private static Manifold CollideBoxSphere(FVector3 boxCenter, FQuaternion boxRotation, FVector3 halfExtents, FVector3 sphereCenter, FP sphereRadius) {
		var invRotation = FQuaternion.Inverse(boxRotation);
		var localCenter = invRotation * (sphereCenter - boxCenter);
		var h = halfExtents;

		var clamped = FVector3.MaxComponents(FVector3.MinComponents(localCenter, h), -h);

		FVector3 localPointOnBox;
		FVector3 localNormal;
		FP separation;

		if (clamped == localCenter) {
			// The sphere center is inside the box: push out along the axis of least penetration.
			var penX = h.X - FP.Abs(localCenter.X);
			var penY = h.Y - FP.Abs(localCenter.Y);
			var penZ = h.Z - FP.Abs(localCenter.Z);

			localPointOnBox = localCenter;
			if (penX <= penY && penX <= penZ) {
				localNormal = new FVector3(localCenter.X >= FP.Zero ? FP.One : -FP.One, FP.Zero, FP.Zero);
				localPointOnBox.X = localCenter.X >= FP.Zero ? h.X : -h.X;
				separation = -penX;
			}
			else if (penY <= penZ) {
				localNormal = new FVector3(FP.Zero, localCenter.Y >= FP.Zero ? FP.One : -FP.One, FP.Zero);
				localPointOnBox.Y = localCenter.Y >= FP.Zero ? h.Y : -h.Y;
				separation = -penY;
			}
			else {
				localNormal = new FVector3(FP.Zero, FP.Zero, localCenter.Z >= FP.Zero ? FP.One : -FP.One);
				localPointOnBox.Z = localCenter.Z >= FP.Zero ? h.Z : -h.Z;
				separation = -penZ;
			}
		}
		else {
			localPointOnBox = clamped;
			var offset = localCenter - clamped;
			var distance = FVector3.Length(offset);
			localNormal = distance > FP.CalculationsEpsilon ? offset / distance : FVector3.Up;
			separation = distance;
		}

		var normal = boxRotation * localNormal;
		var pointOnBox = boxCenter + boxRotation * localPointOnBox;
		var pointOnSphere = sphereCenter - sphereRadius * normal;

		return new Manifold {
			Normal = normal,
			PointCount = 1,
			Point0 = new ManifoldPoint {
				Point = FP.Half * (pointOnBox + pointOnSphere),
				Separation = separation - sphereRadius,
			},
		};
	}

	/// <summary>Closest point on segment [a, b] to point q. Ported from box3d's b3PointToSegmentDistance.</summary>
	private static FVector3 PointToSegmentClosestPoint(FVector3 a, FVector3 b, FVector3 q) {
		var ab = b - a;
		var aq = q - a;

		var alpha = FVector3.Dot(ab, aq);
		if (alpha <= FP.Zero) {
			return a;
		}

		var denominator = FVector3.Dot(ab, ab);
		if (alpha > denominator) {
			return b;
		}

		return a + (alpha / denominator) * ab;
	}

	/// <summary>Closest points between segments [p1, q1] and [p2, q2]. Ported from box3d's b3SegmentDistance.</summary>
	private static (FVector3 pointOnFirst, FVector3 pointOnSecond) SegmentSegmentClosestPoints(FVector3 p1, FVector3 q1, FVector3 p2, FVector3 q2) {
		var d1 = q1 - p1;
		var d2 = q2 - p2;
		var r = p1 - p2;

		var a = FVector3.Dot(d1, d1);
		var b = FVector3.Dot(d1, d2);
		var c = FVector3.Dot(d1, r);
		var e = FVector3.Dot(d2, d2);
		var f = FVector3.Dot(d2, r);

		if (a <= FP.CalculationsEpsilonSqr && e <= FP.CalculationsEpsilonSqr) {
			return (p1, p2);
		}

		if (a <= FP.CalculationsEpsilonSqr) {
			var s2 = FP.Clamp(f / e, FP.Zero, FP.One);
			return (p1, p2 + s2 * d2);
		}

		if (e <= FP.CalculationsEpsilonSqr) {
			var s1 = FP.Clamp(-c / a, FP.Zero, FP.One);
			return (p1 + s1 * d1, p2);
		}

		var denom = a * e - b * b;
		var s1_ = denom > FP.CalculationsEpsilon ? FP.Clamp((b * f - c * e) / denom, FP.Zero, FP.One) : FP.Zero;
		var s2_ = (b * s1_ + f) / e;

		if (s2_ < FP.Zero) {
			s1_ = FP.Clamp(-c / a, FP.Zero, FP.One);
			s2_ = FP.Zero;
		} else if (s2_ > FP.One) {
			s1_ = FP.Clamp((b - c) / a, FP.Zero, FP.One);
			s2_ = FP.One;
		}

		return (p1 + s1_ * d1, p2 + s2_ * d2);
	}

	// ---------------------------------------------------------------------------------------
	// Hull collision. Ported from box3d's convex_manifold.c (b3CollideHullAndCapsule /
	// b3CollideHulls), specialized to a box's fixed 6-face/12-edge/8-vertex topology (see
	// Hull.Faces/Hull.Edges) since every Hull here is a box (see Hull's remarks). The general
	// hull's "shallow penetration" GJK pre-pass and per-contact SAT-axis cache (both pure
	// perf/robustness optimizations, not needed for correctness) are dropped; the SAT face/edge
	// queries and clipping below always run cold, matching this file's other Collide* pairs
	// (also always fully recomputed each tick, no warm feature cache).
	// ---------------------------------------------------------------------------------------

	private struct ClipVertex {
		public FVector3 Position;
		public FP Separation;
	}

	private struct FaceQuery {
		public FP Separation;
		public int FaceIndex;
	}

	private struct EdgeQuery {
		public FP Separation;
		public int EdgeA;
		public int EdgeB;
	}

	private static Manifold CollideHullCapsule(Hull a, Capsule b, FTransform xfBinA) {
		var c1 = FTransform.TransformPoint(xfBinA, b.Center1);
		var c2 = FTransform.TransformPoint(xfBinA, b.Center2);
		return CollideBoxCapsule(a.Center, a.Rotation, a.HalfExtents, c1, c2, b.Radius);
	}

	private static Manifold CollideCapsuleHull(Capsule a, Hull b, FTransform xfBinA) {
		var boxCenter = FTransform.TransformPoint(xfBinA, b.Center);
		var boxRotation = xfBinA.Rotation * b.Rotation;
		var manifold = CollideBoxCapsule(boxCenter, boxRotation, b.HalfExtents, a.Center1, a.Center2, a.Radius);
		// CollideBoxCapsule's normal points from the box toward the capsule. Here the box is the
		// pair's B shape and the capsule is A, so flip to keep this file's A-to-B convention.
		manifold.Normal = -manifold.Normal;
		return manifold;
	}

	private static Manifold CollideHullHull(Hull a, Hull b, FTransform xfBinA) {
		var centerB = FTransform.TransformPoint(xfBinA, b.Center);
		var rotationB = xfBinA.Rotation * b.Rotation;
		return CollideBoxBox(a.Center, a.Rotation, a.HalfExtents, centerB, rotationB, b.HalfExtents);
	}

	/// <summary>Box (zero-radius hull) versus capsule. Ported from box3d's b3CollideHullAndCapsule (SAT branch only).</summary>
	private static Manifold CollideBoxCapsule(FVector3 boxCenter, FQuaternion boxRotation, FVector3 heA, FVector3 c1, FVector3 c2, FP radius) {
		var invRotation = FQuaternion.Inverse(boxRotation);
		var localC1 = invRotation * (c1 - boxCenter);
		var localC2 = invRotation * (c2 - boxCenter);

		var manifold = new Manifold();

		var faceQuery = QueryFaceDirectionsBoxCapsule(heA, localC1, localC2);
		if (faceQuery.Separation > radius) {
			return TransformBoxManifold(manifold, boxCenter, boxRotation);
		}

		var edgeQuery = QueryEdgeDirectionsBoxCapsule(heA, localC1, localC2);
		if (edgeQuery.Separation > radius) {
			return TransformBoxManifold(manifold, boxCenter, boxRotation);
		}

		var faceSeparation = faceQuery.Separation - radius;
		BuildBoxFaceAndCapsuleContact(heA, localC1, localC2, radius, faceQuery.FaceIndex, ref manifold);
		if (manifold.PointCount > 1) {
			// Face contact can be empty if it doesn't realize the axis of minimum penetration; if it
			// found points, be aggressive and compare with the clipped (deepest-point) separation.
			faceSeparation = manifold.MinSeparation();
		}

		var edgeSeparation = edgeQuery.Separation - radius;
		var relEdgeTolerance = FP.FromRatio(90, 100);
		var absTolerance = FP.Half * B3Config.LinearSlop;
		if (manifold.PointCount == 0 || edgeSeparation > relEdgeTolerance * faceSeparation + absTolerance) {
			var edge = Hull.Edges[edgeQuery.EdgeB];
			var pBox = Hull.LocalCorner(heA, edge.V0);
			var qBox = Hull.LocalCorner(heA, edge.V1);

			var edgeManifold = new Manifold();
			if (BuildBoxCapsuleEdgeContact(pBox, qBox - pBox, localC1, localC2 - localC1, radius, ref edgeManifold)) {
				manifold = edgeManifold;
			}
		}

		return TransformBoxManifold(manifold, boxCenter, boxRotation);
	}

	/// <summary>Box versus box. Ported from box3d's b3CollideHulls (cold path, no SAT-axis cache).</summary>
	private static Manifold CollideBoxBox(FVector3 centerA, FQuaternion rotationA, FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB) {
		var invRotationA = FQuaternion.Inverse(rotationA);
		var localCenterB = invRotationA * (centerB - centerA);
		var localRotationB = invRotationA * rotationB;

		var manifold = new Manifold();

		var faceQueryA = QueryFaceDirectionsBoxBox(heA, localCenterB, localRotationB, heB);
		if (faceQueryA.Separation > B3Config.SpeculativeDistance) {
			return TransformBoxManifold(manifold, centerA, rotationA);
		}

		var invLocalRotationB = FQuaternion.Inverse(localRotationB);
		var centerAInB = -(invLocalRotationB * localCenterB);
		var faceQueryB = QueryFaceDirectionsBoxBox(heB, centerAInB, invLocalRotationB, heA);
		if (faceQueryB.Separation > B3Config.SpeculativeDistance) {
			return TransformBoxManifold(manifold, centerA, rotationA);
		}

		var edgeQuery = QueryEdgeDirectionsBoxBox(heA, localCenterB, localRotationB, heB);
		if (edgeQuery.Separation > B3Config.SpeculativeDistance) {
			return TransformBoxManifold(manifold, centerA, rotationA);
		}

		if (faceQueryB.Separation > faceQueryA.Separation + FP.Half * B3Config.LinearSlop) {
			// Face contact B: build in B's own local frame (B as the reference box), then bring the
			// result into A's canonical frame.
			var localManifold = new Manifold();
			BuildBoxFaceContact(heB, centerAInB, invLocalRotationB, heA, faceQueryB.FaceIndex, ref localManifold);
			TransformManifoldBToA(ref localManifold, localCenterB, localRotationB);
			manifold = localManifold;
		}
		else {
			BuildBoxFaceContact(heA, localCenterB, localRotationB, heB, faceQueryA.FaceIndex, ref manifold);
		}

		if (edgeQuery.EdgeA < 0) {
			// There are no valid edge pairs (all edges parallel).
			return TransformBoxManifold(manifold, centerA, rotationA);
		}

		var clippedFaceSeparation = manifold.MinSeparation();
		var relEdgeTolerance = FP.FromRatio(90, 100);
		var absTolerance = FP.Half * B3Config.LinearSlop;

		// Face contact can be empty if it doesn't realize the axis of minimum penetration.
		// Create an edge contact if the face contact failed or the edge contact is significantly better.
		if (manifold.PointCount == 0 || edgeQuery.Separation > relEdgeTolerance * clippedFaceSeparation + absTolerance) {
			var edgeA = Hull.Edges[edgeQuery.EdgeA];
			var pA = Hull.LocalCorner(heA, edgeA.V0);
			var qA = Hull.LocalCorner(heA, edgeA.V1);

			var edgeB = Hull.Edges[edgeQuery.EdgeB];
			var pB = localCenterB + localRotationB * Hull.LocalCorner(heB, edgeB.V0);
			var qB = localCenterB + localRotationB * Hull.LocalCorner(heB, edgeB.V1);

			var edgeManifold = new Manifold();
			if (BuildBoxEdgeContact(pA, qA - pA, pB, qB - pB, ref edgeManifold)) {
				manifold = edgeManifold;
			}
		}

		return TransformBoxManifold(manifold, centerA, rotationA);
	}

	/// <summary>Transforms a manifold out of a box's own canonical (unrotated, centered) frame and into the frame <paramref name="boxCenter"/>/<paramref name="boxRotation"/> live in.</summary>
	private static Manifold TransformBoxManifold(Manifold manifold, FVector3 boxCenter, FQuaternion boxRotation) {
		manifold.Normal = boxRotation * manifold.Normal;
		for (var i = 0; i < manifold.PointCount; i++) {
			var point = manifold.GetPoint(i);
			point.Point = boxCenter + boxRotation * point.Point;
			manifold.SetPoint(i, point);
		}

		return manifold;
	}

	/// <summary>
	/// Transforms a manifold built with hull B as the reference (so its geometry is in B's own
	/// local frame and its normal points away from B) into hull A's canonical frame, flipping the
	/// normal to this file's A-to-B convention. Ported from box3d's b3BuildFaceBContact.
	/// </summary>
	private static void TransformManifoldBToA(ref Manifold manifold, FVector3 centerB, FQuaternion rotationB) {
		manifold.Normal = -(rotationB * manifold.Normal);
		for (var i = 0; i < manifold.PointCount; i++) {
			var point = manifold.GetPoint(i);
			point.Point = centerB + rotationB * point.Point;
			manifold.SetPoint(i, point);
		}
	}

	/// <summary>Face SAT query: for each face of box A (half-extents <paramref name="heA"/>, at the origin), the separation to box B's support point. Ported from box3d's b3QueryFaceDirections.</summary>
	private static FaceQuery QueryFaceDirectionsBoxBox(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB) {
		var best = new FaceQuery { Separation = FP.MinValue, FaceIndex = 0 };

		for (var f = 0; f < 6; f++) {
			var normal = Hull.Faces[f].Normal;
			var offset = FVector3.Dot(FVector3.AbsComponents(normal), heA);

			var localDirection = FQuaternion.Inverse(rotationB) * -normal;
			var supportLocal = Hull.SupportLocal(heB, localDirection);
			var supportWorld = centerB + rotationB * supportLocal;

			var separation = FVector3.Dot(normal, supportWorld) - offset;
			if (separation > best.Separation) {
				best = new FaceQuery { Separation = separation, FaceIndex = f };
			}
		}

		return best;
	}

	/// <summary>Face SAT query: for each face of box A, the separation to the capsule's nearer endpoint. Ported from box3d's b3QueryFaceDirectionHullAndCapsule.</summary>
	private static FaceQuery QueryFaceDirectionsBoxCapsule(FVector3 heA, FVector3 c1, FVector3 c2) {
		var best = new FaceQuery { Separation = FP.MinValue, FaceIndex = 0 };

		for (var f = 0; f < 6; f++) {
			var normal = Hull.Faces[f].Normal;
			var offset = FVector3.Dot(FVector3.AbsComponents(normal), heA);

			var separation = FP.Min(FVector3.Dot(normal, c1), FVector3.Dot(normal, c2)) - offset;
			if (separation > best.Separation) {
				best = new FaceQuery { Separation = separation, FaceIndex = f };
			}
		}

		return best;
	}

	/// <summary>Edge SAT query between box A's 12 edges and box B's 12 edges. Ported from box3d's b3QueryEdgeDirections.</summary>
	private static EdgeQuery QueryEdgeDirectionsBoxBox(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB) {
		var best = new EdgeQuery { Separation = FP.MinValue, EdgeA = -1, EdgeB = -1 };

		for (var j = 0; j < Hull.Edges.Length; j++) {
			var edgeB = Hull.Edges[j];
			var pB = centerB + rotationB * Hull.LocalCorner(heB, edgeB.V0);
			var qB = centerB + rotationB * Hull.LocalCorner(heB, edgeB.V1);
			var eB = qB - pB;
			var uB = rotationB * Hull.Faces[edgeB.FaceA].Normal;
			var vB = rotationB * Hull.Faces[edgeB.FaceB].Normal;

			for (var i = 0; i < Hull.Edges.Length; i++) {
				var edgeA = Hull.Edges[i];
				var pA = Hull.LocalCorner(heA, edgeA.V0);
				var qA = Hull.LocalCorner(heA, edgeA.V1);
				var eA = qA - pA;
				var uA = Hull.Faces[edgeA.FaceA].Normal;
				var vA = Hull.Faces[edgeA.FaceB].Normal;

				if (!IsMinkowskiFace(uA, vA, eA, uB, vB, eB)) {
					continue;
				}

				if (!TryEdgeEdgeSeparation(pA, eA, FVector3.Zero, pB, eB, centerB, out var separation)) {
					continue;
				}

				if (separation > best.Separation) {
					best = new EdgeQuery { Separation = separation, EdgeA = i, EdgeB = j };
				}
			}
		}

		return best;
	}

	/// <summary>Edge SAT query between box A's 12 edges and the capsule's single axis. Ported from box3d's b3QueryEdgeDirectionHullAndCapsule.</summary>
	private static EdgeQuery QueryEdgeDirectionsBoxCapsule(FVector3 heA, FVector3 c1, FVector3 c2) {
		var best = new EdgeQuery { Separation = FP.MinValue, EdgeA = -1, EdgeB = -1 };
		var e1 = c2 - c1;
		var capsuleCenter = FP.Half * (c1 + c2);

		for (var i = 0; i < Hull.Edges.Length; i++) {
			var edge = Hull.Edges[i];
			var u2 = Hull.Faces[edge.FaceA].Normal;
			var v2 = Hull.Faces[edge.FaceB].Normal;

			if (!IsMinkowskiFaceIsolated(u2, v2, e1)) {
				continue;
			}

			var p2 = Hull.LocalCorner(heA, edge.V0);
			var q2 = Hull.LocalCorner(heA, edge.V1);
			var e2 = q2 - p2;

			if (!TryEdgeEdgeSeparation(c1, e1, capsuleCenter, p2, e2, FVector3.Zero, out var separation)) {
				continue;
			}

			if (separation > best.Separation) {
				best = new EdgeQuery { Separation = separation, EdgeA = -1, EdgeB = i };
			}
		}

		return best;
	}

	/// <summary>
	/// An isolated edge (like a capsule's single axis) defines a circle through the origin on the
	/// Gauss map. Testing for overlap between this circle and the arc AB simplifies to a plane
	/// test. Ported from box3d's b3IsMinkowskiFaceIsolated.
	/// </summary>
	private static bool IsMinkowskiFaceIsolated(FVector3 a, FVector3 b, FVector3 n) {
		var an = FVector3.Dot(a, n);
		var bn = FVector3.Dot(b, n);
		return an * bn <= FP.Zero;
	}

	/// <summary>
	/// Two edges build a face on the Minkowski sum if their associated arcs (defined by each edge's
	/// two adjacent face normals) intersect on the Gauss map. Ported from box3d's b3IsMinkowskiFace
	/// (inlined as in b3QueryEdgeDirections), with one deviation: box3d passes the raw (unnormalized)
	/// edge vectors and compares the four cross-quadrant products against exactly zero, relying on
	/// its per-contact SAT-axis cache (dropped here, see this file's remarks) to avoid re-deriving
	/// the edge query from scratch once a face contact is stable. Without that cache, two boxes
	/// resting axis-aligned sit exactly on this test's degenerate boundary (an edge-pair axis that
	/// coincides with a face axis, which should always lose to the face test) — small fixed-point
	/// rotation noise can then flip a product's sign and spuriously validate a bogus edge axis,
	/// which convex_manifold.c's b3EdgeEdgeSeparation formula happily "confirms" with an unrelated
	/// (frequently large) separation for those two particular edges, killing an otherwise-solid
	/// resting contact. Normalizing the edge vectors first bounds the four products to [-1, 1]
	/// regardless of box size, so a small fixed epsilon reliably rejects that boundary case.
	/// </summary>
	private static bool IsMinkowskiFace(FVector3 uA, FVector3 vA, FVector3 eA, FVector3 uB, FVector3 vB, FVector3 eB) {
		var normEA = FVector3.NormalizeSafe(eA);
		var normEB = FVector3.NormalizeSafe(eB);

		var cba = FVector3.Dot(uB, normEA);
		var dba = FVector3.Dot(vB, normEA);
		var adc = -FVector3.Dot(uA, normEB);
		var bdc = -FVector3.Dot(vA, normEB);

		var epsilon = FP.FromRatio(1, 100);
		return cba * dba < -epsilon && adc * bdc < -epsilon && cba * bdc > epsilon;
	}

	/// <summary>
	/// Separation of two edges along their common normal cross(e1, e2), oriented away from whichever
	/// centroid gives the more reliable (larger-magnitude) sign. Returns false for near-parallel
	/// edges, where this axis isn't meaningful. Ported from box3d's b3EdgeEdgeSeparation.
	/// </summary>
	private static bool TryEdgeEdgeSeparation(FVector3 p1, FVector3 e1, FVector3 c1, FVector3 p2, FVector3 e2, FVector3 c2, out FP separation) {
		var u = FVector3.Cross(e1, e2);
		var lengthSqr = FVector3.LengthSqr(u);

		// Skip near-parallel edges: |e1 x e2| = sin(alpha) * |e1| * |e2|. Tolerance matches box3d's kTolerance (0.005).
		var toleranceSqr = FP.FromRatio(25, 1000000);
		if (lengthSqr < toleranceSqr * FVector3.LengthSqr(e1) * FVector3.LengthSqr(e2) || lengthSqr < FP.CalculationsEpsilonSqr) {
			separation = FP.Zero;
			return false;
		}

		var n = u / FP.Sqrt(lengthSqr);

		// Make sure the normal points away from the first shape. Pick whichever centroid gives the
		// more significant (less noise-prone) sign, matching box3d's tie-breaking.
		var sign1 = FVector3.Dot(n, p1 - c1);
		var sign2 = FVector3.Dot(n, p2 - c2);
		if (FP.Abs(sign1) > FP.Abs(sign2)) {
			if (sign1 < FP.Zero) n = -n;
		}
		else if (sign2 > FP.Zero) {
			n = -n;
		}

		separation = FVector3.Dot(n, p2 - p1);
		return true;
	}

	/// <summary>Clips a 2-point segment against a single plane, keeping the side with separation &lt;= 0. Ported from box3d's b3ClipSegment.</summary>
	private static int ClipSegment(ClipVertex[] segment, FPlane plane) {
		var vertex1 = segment[0];
		var vertex2 = segment[1];
		var distance1 = FPlane.Separation(plane, vertex1.Position);
		var distance2 = FPlane.Separation(plane, vertex2.Position);

		var count = 0;
		if (distance1 <= FP.Zero) {
			segment[count++] = vertex1;
		}

		if (distance2 <= FP.Zero) {
			segment[count++] = vertex2;
		}

		if (distance1 * distance2 < FP.Zero) {
			var fraction = distance1 / (distance1 - distance2);
			segment[count++] = new ClipVertex { Position = vertex1.Position + fraction * (vertex2.Position - vertex1.Position) };
		}

		return count;
	}

	/// <summary>Clips a 2-point segment against all 4 side planes of a box face. Ported from box3d's b3ClipSegmentToHullFace.</summary>
	private static int ClipSegmentToBoxFace(ClipVertex[] segment, FVector3 heA, int faceIndex) {
		var face = Hull.Faces[faceIndex];
		Span<int> loop = stackalloc int[4] { face.V0, face.V1, face.V2, face.V3 };

		for (var i = 0; i < 4; i++) {
			var v1 = Hull.LocalCorner(heA, loop[i]);
			var v2 = Hull.LocalCorner(heA, loop[(i + 1) % 4]);
			var tangent = FVector3.Normalize(v2 - v1);
			var binormal = FVector3.Cross(tangent, face.Normal);
			var plane = FPlane.FromNormalAndPoint(binormal, v1);

			if (ClipSegment(segment, plane) < 2) {
				return 0;
			}
		}

		return 2;
	}

	/// <summary>Sutherland-Hodgman clip of a convex polygon against a single plane, tracking each surviving/new vertex's separation from <paramref name="refPlane"/>. Ported from box3d's b3ClipPolygon.</summary>
	private static int ClipPolygon(ClipVertex[] output, ClipVertex[] input, int count, FPlane clipPlane, FPlane refPlane) {
		var vertex1 = input[count - 1];
		var distance1 = FPlane.Separation(clipPlane, vertex1.Position);
		var outCount = 0;

		for (var index = 0; index < count; index++) {
			var vertex2 = input[index];
			var distance2 = FPlane.Separation(clipPlane, vertex2.Position);

			if (distance1 <= FP.Zero && distance2 <= FP.Zero) {
				output[outCount++] = vertex2;
			}
			else if (distance1 <= FP.Zero && distance2 > FP.Zero) {
				var fraction = distance1 / (distance1 - distance2);
				var position = vertex1.Position + fraction * (vertex2.Position - vertex1.Position);
				output[outCount++] = new ClipVertex { Position = position, Separation = FPlane.Separation(refPlane, position) };
			}
			else if (distance2 <= FP.Zero && distance1 > FP.Zero) {
				var fraction = distance1 / (distance1 - distance2);
				var position = vertex1.Position + fraction * (vertex2.Position - vertex1.Position);
				output[outCount++] = new ClipVertex { Position = position, Separation = FPlane.Separation(refPlane, position) };
				output[outCount++] = vertex2;
			}

			vertex1 = vertex2;
			distance1 = distance2;
		}

		return outCount;
	}

	/// <summary>
	/// Builds a face contact with box "ref" (half-extents <paramref name="heRef"/>, at the origin)
	/// as the reference: finds box "inc"'s most anti-parallel face, clips its polygon against the
	/// reference face's 4 side planes, and reduces to <see cref="MaxPoints"/> points. Result is in
	/// the reference box's own frame, with the normal pointing away from it. Ported from box3d's
	/// b3BuildFaceAContact (b3FindIncidentFace inlined as "most anti-parallel face", valid for a
	/// box's regular topology).
	/// </summary>
	private static bool BuildBoxFaceContact(FVector3 heRef, FVector3 centerInc, FQuaternion rotationInc, FVector3 heInc, int refFaceIndex, ref Manifold manifold) {
		var refFace = Hull.Faces[refFaceIndex];
		var refNormal = refFace.Normal;
		var refOffset = FVector3.Dot(FVector3.AbsComponents(refNormal), heRef);
		var refPlane = new FPlane(refNormal, refOffset);

		var refNormalInInc = FQuaternion.Inverse(rotationInc) * refNormal;
		var incFaceIndex = 0;
		var minDot = FP.MaxValue;
		for (var f = 0; f < 6; f++) {
			var dot = FVector3.Dot(Hull.Faces[f].Normal, refNormalInInc);
			if (dot < minDot) {
				minDot = dot;
				incFaceIndex = f;
			}
		}

		var incFace = Hull.Faces[incFaceIndex];
		Span<int> incLoop = stackalloc int[4] { incFace.V0, incFace.V1, incFace.V2, incFace.V3 };

		var buffer1 = new ClipVertex[8];
		var buffer2 = new ClipVertex[8];
		var count = 4;
		for (var i = 0; i < 4; i++) {
			var world = centerInc + rotationInc * Hull.LocalCorner(heInc, incLoop[i]);
			buffer1[i] = new ClipVertex { Position = world, Separation = FPlane.Separation(refPlane, world) };
		}

		Span<int> refLoop = stackalloc int[4] { refFace.V0, refFace.V1, refFace.V2, refFace.V3 };
		var input = buffer1;
		var output = buffer2;
		for (var i = 0; i < 4; i++) {
			var v1 = Hull.LocalCorner(heRef, refLoop[i]);
			var v2 = Hull.LocalCorner(heRef, refLoop[(i + 1) % 4]);
			var tangent = FVector3.Normalize(v2 - v1);
			var binormal = FVector3.Cross(tangent, refNormal);
			var clipPlane = FPlane.FromNormalAndPoint(binormal, v1);

			count = ClipPolygon(output, input, count, clipPlane, refPlane);
			(input, output) = (output, input);

			if (count < 3) {
				return false;
			}
		}

		var points = new ManifoldPoint[count];
		var minSeparation = FP.MaxValue;
		for (var i = 0; i < count; i++) {
			var clipPoint = input[i];
			// The half-way point keeps points in the same position whether A or B ends up the reference face.
			var point = clipPoint.Position - FP.Half * clipPoint.Separation * refNormal;
			points[i] = new ManifoldPoint { Point = point, Separation = clipPoint.Separation };
			minSeparation = FP.Min(minSeparation, clipPoint.Separation);
		}

		if (minSeparation >= B3Config.SpeculativeDistance) {
			return false;
		}

		ReduceManifoldPoints(points, count, refNormal, ref manifold);
		return manifold.PointCount > 0;
	}

	/// <summary>Builds a 2-point face contact by clipping the capsule's segment against box A's reference face. Ported from box3d's b3BuildHullFaceAndCapsuleContact.</summary>
	private static bool BuildBoxFaceAndCapsuleContact(FVector3 heA, FVector3 c1, FVector3 c2, FP radius, int refFaceIndex, ref Manifold manifold) {
		var refFace = Hull.Faces[refFaceIndex];
		var refNormal = refFace.Normal;
		var refOffset = FVector3.Dot(FVector3.AbsComponents(refNormal), heA);
		var refPlane = new FPlane(refNormal, refOffset);

		var segment = new[] { new ClipVertex { Position = c1 }, new ClipVertex { Position = c2 } };
		if (ClipSegmentToBoxFace(segment, heA, refFaceIndex) < 2) {
			return false;
		}

		var distance1 = FPlane.Separation(refPlane, segment[0].Position);
		var distance2 = FPlane.Separation(refPlane, segment[1].Position);

		// distance1/distance2 measure the capsule's core segment (not yet radius-adjusted, see the
		// ManifoldPoint.Separation assignments below) against the face plane, so the threshold needs
		// +radius too -- box3d's own b3CollideHullAndCapsule compares against
		// `capsuleB->radius + speculativeDistance`, not speculativeDistance alone. Without this, any
		// capsule resting normally (core segment ~radius away from the surface, which is the *normal*
		// configuration for any resting contact) has its contact silently dropped.
		if (distance1 > radius + B3Config.SpeculativeDistance && distance2 > radius + B3Config.SpeculativeDistance) {
			return false;
		}

		var point1 = segment[0].Position - FP.Half * (distance1 + radius) * refNormal;
		var point2 = segment[1].Position - FP.Half * (distance2 + radius) * refNormal;

		manifold.Normal = refNormal;
		manifold.PointCount = 2;
		manifold.SetPoint(0, new ManifoldPoint { Point = point1, Separation = distance1 - radius });
		manifold.SetPoint(1, new ManifoldPoint { Point = point2, Separation = distance2 - radius });
		return true;
	}

	/// <summary>Builds a 1-point edge-edge contact between two (already-positioned) finite edges. Ported from box3d's b3BuildEdgeContact.</summary>
	private static bool BuildBoxEdgeContact(FVector3 pA, FVector3 eA, FVector3 pB, FVector3 eB, ref Manifold manifold) {
		var normal = FVector3.Cross(eA, eB);
		var lengthSqr = FVector3.LengthSqr(normal);
		if (lengthSqr < FP.CalculationsEpsilonSqr) {
			return false;
		}

		normal /= FP.Sqrt(lengthSqr);
		if (FVector3.Dot(normal, pA) < FP.Zero) {
			// Box A's canonical center is the origin, so this is normal . (pA - centerA).
			normal = -normal;
		}

		var result = FVector3.LineDistance(pA, eA, pB, eB);
		if (result.Fraction1 < FP.Zero || result.Fraction1 > FP.One || result.Fraction2 < FP.Zero || result.Fraction2 > FP.One) {
			// Closest points fall beyond the finite edges.
			return false;
		}

		var separation = FVector3.Dot(normal, result.Point2 - result.Point1);
		var point = FP.Half * (result.Point1 + result.Point2);

		manifold.Normal = normal;
		manifold.PointCount = 1;
		manifold.SetPoint(0, new ManifoldPoint { Point = point, Separation = separation });
		return true;
	}

	/// <summary>Builds a 1-point edge-edge contact between a box edge and the capsule's axis. Ported from box3d's b3BuildHullAndCapsuleEdgeContact.</summary>
	private static bool BuildBoxCapsuleEdgeContact(FVector3 pBox, FVector3 eBox, FVector3 pCapsule, FVector3 eCapsule, FP radius, ref Manifold manifold) {
		var normal = FVector3.Cross(eCapsule, eBox);
		var lengthSqr = FVector3.LengthSqr(normal);
		if (lengthSqr < FP.CalculationsEpsilonSqr) {
			return false;
		}

		normal /= FP.Sqrt(lengthSqr);
		if (FVector3.Dot(normal, pBox) < FP.Zero) {
			// Box A's canonical center is the origin, so this is normal . (pBox - centerA).
			normal = -normal;
		}

		var result = FVector3.LineDistance(pBox, eBox, pCapsule, eCapsule);
		if (result.Fraction1 < FP.Zero || result.Fraction1 > FP.One || result.Fraction2 < FP.Zero || result.Fraction2 > FP.One) {
			return false;
		}

		var separation = FVector3.Dot(normal, result.Point2 - result.Point1) - radius;
		var point = FP.Half * ((result.Point1 - radius * normal) + result.Point2);

		manifold.Normal = normal;
		manifold.PointCount = 1;
		manifold.SetPoint(0, new ManifoldPoint { Point = point, Separation = separation });
		return true;
	}

	/// <summary>
	/// Reduces an arbitrary-size candidate point set to at most <see cref="MaxPoints"/>, biased
	/// toward the deepest point and then toward maximum spread/area so the surviving points support
	/// a stable rest contact rather than clustering. Mutates <paramref name="points"/> (swap-remove)
	/// as it selects. Ported from box3d's b3ReduceManifoldPoints.
	/// </summary>
	private static void ReduceManifoldPoints(ManifoldPoint[] points, int count, FVector3 normal, ref Manifold manifold) {
		manifold.Normal = normal;
		manifold.PointCount = 0;

		if (count <= MaxPoints) {
			for (var i = 0; i < count; i++) {
				manifold.SetPoint(i, points[i]);
			}

			manifold.PointCount = count;
			return;
		}

		var speculativeDistance = B3Config.SpeculativeDistance;
		var tolSqr = speculativeDistance * speculativeDistance;
		// Biases the search toward the current best pick, avoiding flicker between near-tied candidates across ticks.
		var bias = FP.FromRatio(95, 100);

		// Step 1: find the deepest touching point.
		var searchDirection = FVector3.Perp(normal);
		var bestIndex = -1;
		var bestScore = FP.MinValue;
		for (var index = 0; index < count; index++) {
			if (points[index].Separation > speculativeDistance) {
				continue;
			}

			var score = -points[index].Separation + FVector3.Dot(searchDirection, points[index].Point);
			if (bias * score > bestScore) {
				bestScore = score;
				bestIndex = index;
			}
		}

		if (bestIndex < 0) {
			return;
		}

		manifold.SetPoint(0, points[bestIndex]);
		manifold.PointCount = 1;
		points[bestIndex] = points[count - 1];
		count -= 1;

		var a = manifold.Point0.Point;

		// Step 2: find the farthest point in 2D (projected onto the reference plane).
		bestScore = FP.Zero;
		bestIndex = -1;
		for (var index = 0; index < count; index++) {
			var p = points[index].Point;
			var d = p - a;
			var v = d - FVector3.Dot(d, normal) * normal;
			var distanceSqr = FVector3.LengthSqr(v);
			var separation = FP.Max(FP.Zero, -points[index].Separation);
			var score = distanceSqr + 4 * separation * separation;
			if (bias * score > bestScore) {
				bestScore = score;
				bestIndex = index;
			}
		}

		if (bestScore < tolSqr) {
			return;
		}

		manifold.SetPoint(1, points[bestIndex]);
		manifold.PointCount = 2;
		points[bestIndex] = points[count - 1];
		count -= 1;

		var b = manifold.Point1.Point;

		// Step 3: find the point with the maximum triangle area against a-b.
		bestScore = tolSqr;
		bestIndex = -1;
		var bestSignedArea = FP.Zero;
		var ba = b - a;
		for (var index = 0; index < count; index++) {
			var p = points[index].Point;
			var signedArea = FVector3.Dot(normal, FVector3.Cross(ba, p - a));
			var score = FP.Abs(signedArea);
			if (bias * score >= bestScore) {
				bestScore = score;
				bestIndex = index;
				bestSignedArea = signedArea;
			}
		}

		if (bestIndex < 0) {
			return;
		}

		manifold.SetPoint(2, points[bestIndex]);
		manifold.PointCount = 3;
		points[bestIndex] = points[count - 1];
		count -= 1;

		var c = manifold.Point2.Point;

		// Step 4: find the point that adds the most area outside the current triangle.
		bestScore = tolSqr;
		bestIndex = -1;
		var sign = bestSignedArea < FP.Zero ? -FP.One : FP.One;
		for (var index = 0; index < count; index++) {
			var p = points[index].Point;
			var u1 = sign * FVector3.Dot(normal, FVector3.Cross(p - a, ba));
			var u2 = sign * FVector3.Dot(normal, FVector3.Cross(p - b, c - b));
			var u3 = sign * FVector3.Dot(normal, FVector3.Cross(p - c, a - c));
			var score = FP.Max(u1, FP.Max(u2, u3));
			if (bias * score > bestScore) {
				bestScore = score;
				bestIndex = index;
			}
		}

		if (bestIndex >= 0) {
			manifold.SetPoint(manifold.PointCount, points[bestIndex]);
			manifold.PointCount += 1;
		}
	}
}
