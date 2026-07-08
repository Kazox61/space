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
/// sphere/capsule, capsule/capsule, and hull/sphere (the only shape pairs in scope for this pass)
/// never need more than one contact point in the general case — box3d's own b3FeaturePair_single
/// path for round shapes. box3d's capsule/capsule *does* add a second point when the capsules are
/// nearly parallel (for resting stability); that refinement is deferred here (see <see cref="Collide"/>).
/// Deferred entirely: hull/capsule and hull/hull. A box resting flat needs a multi-point manifold
/// (box3d clips the reference face against the incident feature) to stay stable, which this
/// single-point <see cref="Manifold"/> can't represent — so those pairs still fall through rather
/// than settle. Box vs sphere/box vs ground-sphere works because a sphere only ever needs one point.
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
			(ShapeType.Hull, ShapeType.Sphere) => CollideHullSphere(a.HullShape, b.SphereShape, xfBinA),
			(ShapeType.Sphere, ShapeType.Hull) => CollideSphereHull(a.SphereShape, b.HullShape, xfBinA),
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
}
