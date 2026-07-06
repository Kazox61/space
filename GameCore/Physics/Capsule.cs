using Fixed32;

namespace Space.GameCore;

/// <summary>
/// A solid capsule: two hemispheres connected by a cylinder.
/// </summary>
public struct Capsule {
	/// <summary>Local center of the first hemisphere.</summary>
	public FVector3 Center1;

	/// <summary>Local center of the second hemisphere.</summary>
	public FVector3 Center2;

	/// <summary>The radius of the hemispheres.</summary>
	public FP Radius;

	public Capsule(FVector3 center1, FVector3 center2, FP radius) {
		Center1 = center1;
		Center2 = center2;
		Radius = radius;
	}

	/// <summary>Compute mass properties of a capsule.</summary>
	public static MassData ComputeMass(Capsule shape, FP density) {
		var c1 = shape.Center1;
		var c2 = shape.Center2;
		var r = shape.Radius;

		// Cylinder.
		var cylinderHeight = FVector3.Distance(c1, c2);
		var cylinderVolume = FP.Pi * r * r * cylinderHeight;
		var cylinderMass = cylinderVolume * density;

		// Sphere.
		var sphereVolume = FP.FromRatio(4, 3) * FP.Pi * r * r * r;
		var sphereMass = sphereVolume * density;

		// Local accumulated inertia.
		var inertia = FMatrix3.CylinderInertia(cylinderMass, r, cylinderHeight) + FMatrix3.SphereInertia(sphereMass, r);

		var steiner = FP.FromRatio(1, 8) * sphereMass * (3 * r + 2 * cylinderHeight) * cylinderHeight;
		inertia.Cx.X += steiner;
		inertia.Cz.Z += steiner;

		// Align the capsule axis with the chosen up-axis.
		var rotation = FMatrix3.Identity;
		if (cylinderHeight * cylinderHeight > FP.CalculationsEpsilonSqr) {
			var direction = FVector3.Normalize(c2 - c1);
			var q = FQuaternion.ComputeBetweenUnitVectors(FVector3.Up, direction);
			rotation = FMatrix3.FromQuaternion(q);
		}

		var mass = sphereMass + cylinderMass;
		var center = FP.Half * (c1 + c2);

		return new MassData {
			Mass = mass,
			Center = center,
			// Rotate the central inertia into the shape frame.
			Inertia = rotation * inertia * FMatrix3.Transpose(rotation),
		};
	}

	/// <summary>Compute the bounding box of a transformed capsule.</summary>
	public static FAABB ComputeAABB(Capsule shape, FTransform transform) {
		var r = shape.Radius;

		var center1 = FTransform.TransformPoint(transform, shape.Center1);
		var center2 = FTransform.TransformPoint(transform, shape.Center2);
		var extent = new FVector3(r, r, r);

		return new FAABB(FVector3.MinComponents(center1, center2) - extent, FVector3.MaxComponents(center1, center2) + extent);
	}

	/// <summary>Overlap test between this capsule and a generic shape proxy.</summary>
	public static bool Overlap(Capsule shape, FTransform shapeTransform, ShapeProxy proxy) {
		var input = new DistanceInput {
			ProxyA = new ShapeProxy { Points = new[] { shape.Center1, shape.Center2 }, Radius = shape.Radius },
			ProxyB = proxy,
			Transform = FTransform.Invert(shapeTransform),
			UseRadii = true,
		};

		var cache = SimplexCache.Empty;
		var output = Distance.ShapeDistance(input, ref cache);
		return output.Distance < B3Config.OverlapSlop;
	}

	/// <summary>Shape cast versus a capsule. Initial overlap is treated as a hit; see <see cref="Distance.ShapeCast"/>.</summary>
	public static CastOutput ShapeCast(Capsule shape, ShapeCastInput input) {
		var pairInput = new ShapeCastPairInput {
			ProxyA = new ShapeProxy { Points = new[] { shape.Center1, shape.Center2 }, Radius = shape.Radius },
			ProxyB = input.Proxy,
			Transform = FTransform.Identity,
			TranslationB = input.Translation,
			MaxFraction = input.MaxFraction,
			CanEncroach = input.CanEncroach,
		};

		return Distance.ShapeCast(pairInput);
	}

	/// <summary>Compute the bounding box of a capsule swept between two transforms.</summary>
	public static FAABB ComputeSweptAABB(Capsule shape, FTransform xf1, FTransform xf2) {
		var r = new FVector3(shape.Radius, shape.Radius, shape.Radius);
		var a = FTransform.TransformPoint(xf1, shape.Center1);
		var b = FTransform.TransformPoint(xf1, shape.Center2);
		var c = FTransform.TransformPoint(xf2, shape.Center1);
		var d = FTransform.TransformPoint(xf2, shape.Center2);

		var lower = FVector3.MinComponents(FVector3.MinComponents(a, b), FVector3.MinComponents(c, d));
		var upper = FVector3.MaxComponents(FVector3.MaxComponents(a, b), FVector3.MaxComponents(c, d));

		return new FAABB(lower - r, upper + r);
	}

	/// <summary>
	/// Ray cast versus capsule in local space. A zero length ray is a point query. Initial overlap
	/// reports a hit at the ray origin with zero fraction and zero normal.
	/// </summary>
	public static CastOutput RayCast(Capsule shape, RayCastInput input) {
		var c1 = shape.Center1;
		var c2 = shape.Center2;
		var r = shape.Radius;

		var output = new CastOutput();

		var d = c2 - c1;

		// Fall back to a sphere if the capsule is short.
		var tol = FP.FromRatio(1, 100) * B3Config.LinearSlop;
		var lengthSquared = FVector3.LengthSqr(d);
		if (lengthSquared < tol * tol) {
			var sphereCenter = FP.Half * (shape.Center1 + shape.Center2);
			return Sphere.RayCast(new Sphere(sphereCenter, shape.Radius), input);
		}

		// Vector from the first center to the ray origin.
		var s = input.Origin - c1;

		// Capsule axis.
		var length = FP.Sqrt(lengthSquared);
		var axis = d / length;

		// Project the ray origin onto the capsule axis.
		var u = FVector3.Dot(s, axis);

		// Closest point on the infinite capsule axis, relative to c1.
		var c = u * axis;

		// Vector from the closest point to the ray origin.
		var sc = s - c;

		// Squared distance from the ray origin to the capsule axis.
		var sc2 = FVector3.LengthSqr(sc);

		// Is the ray origin within the infinite cylinder along the capsule axis?
		if (sc2 < r * r) {
			// Clamped barycentric coordinate of the ray origin projected onto the capsule axis.
			var uClamped = FP.Clamp(u, FP.Zero, length);

			// The closest point on the bounded capsule segment, relative to c1.
			var cp = uClamped * axis;

			// Vector from the ray origin to the closest point on the segment.
			var scp = s - cp;

			// Squared distance of the ray origin from the capsule segment.
			var scp2 = FVector3.LengthSqr(scp);

			// Is the ray origin within the capsule?
			if (scp2 < r * r) {
				output.Hit = true;
				output.Point = input.Origin;
				return output;
			}

			// The ray can hit an end cap.
			return Sphere.RayCast(new Sphere(c1 + cp, r), input);
		}

		// Ray axis. A zero length ray reaching here starts outside the capsule, so it misses.
		var dr = input.Translation;
		var rayAxis = FVector3.GetLengthAndNormalize(dr, out var rayLength);
		if (rayLength == FP.Zero) {
			return output;
		}

		// Barycentric coordinate of the ray end point.
		var v = u + input.MaxFraction * FVector3.Dot(dr, axis);

		// Early out: does the projected ray fall outside the capsule?
		if ((u < -r && v < -r) || (length + r < u && length + r < v)) {
			return output;
		}

		// Compute the closest point between the ray segment and the capsule segment.
		// See Real-Time Collision Detection, section 5.1.9.
		var a1 = axis;
		var a2 = rayAxis;
		var a12 = FVector3.Dot(a1, a2);

		// Ray distance to the near intersection with the infinite cylinder. Length units.
		FP tr;

		var det = FP.One - a12 * a12;
		if (det < FP.CalculationsEpsilon) {
			// Ray and capsule axes are (near) parallel. Solve the 2D problem of ray versus circle
			// starting at the ray origin, where the circle is the axial view of the infinite capsule cylinder.
			var perp = a2 - a12 * a1;
			var perp2 = FVector3.LengthSqr(perp);

			var beta = FVector3.Dot(sc, perp);
			var gamma = sc2 - r * r;
			var disc = beta * beta - perp2 * gamma;

			if (beta >= FP.Zero || disc < FP.Zero) {
				return output;
			}

			tr = gamma / (-beta + FP.Sqrt(disc));
		}
		else {
			// Ray and capsule axes are not parallel.
			var invDet = FP.One / det;
			var sa1 = u;
			var sa2 = FVector3.Dot(s, a2);

			var t1 = (sa1 - a12 * sa2) * invDet;
			var t2 = (a12 * sa1 - sa2) * invDet;

			var p1 = t1 * a1;
			var p2 = s + t2 * a2;

			var g = p2 - p1;
			var g2 = FVector3.LengthSqr(g);
			if (g2 > r * r) {
				// Closest point on the infinite ray is outside the infinite cylinder.
				return output;
			}

			var h = FP.Sqrt((r * r - g2) * invDet);
			tr = t2 - h;
		}

		if (tr < FP.Zero || input.MaxFraction * rayLength < tr) {
			return output;
		}

		// The corresponding distance on the capsule axis. Length units.
		var tc = u + tr * a12;

		if (tc < FP.Zero) {
			return Sphere.RayCast(new Sphere(c1, r), input);
		}

		if (length < tc) {
			return Sphere.RayCast(new Sphere(c2, r), input);
		}

		// Hit point on the capsule side, relative to c1.
		var p = s + tr * rayAxis;
		var normal = FVector3.Normalize(p - tc * axis);

		output.Point = c1 + p;
		output.Normal = normal;
		output.Fraction = FP.Clamp(tr / rayLength, FP.Zero, input.MaxFraction);
		output.Hit = true;
		return output;
	}
}
