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
/// A contact manifold between two shapes. Bounded to a single point: sphere/sphere,
/// sphere/capsule, and capsule/capsule (the only shape pairs in scope for this pass) never need
/// more than one contact point in the general case — box3d's own b3FeaturePair_single path for
/// round shapes. box3d's capsule/capsule *does* add a second point when the capsules are nearly
/// parallel (for resting stability); that refinement is deferred here (see <see cref="Collide"/>).
/// </summary>
public struct Manifold {
	public FVector3 Normal;
	public int PointCount;
	public ManifoldPoint Point0;

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
			_ => default,
		};
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
}
