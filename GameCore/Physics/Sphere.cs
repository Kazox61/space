using Fixed32;

namespace Space.GameCore;

public struct Sphere {
	/// <summary>The local center.</summary>
	public FVector3 Center;

	/// <summary>The radius.</summary>
	public FP Radius;

	public Sphere(FVector3 center, FP radius) {
		Center = center;
		Radius = radius;
	}

	/// <summary>Compute mass properties of a sphere.</summary>
	public static MassData ComputeMass(Sphere shape, FP density) {
		var radius = shape.Radius;
		var volume = FP.FromRatio(4, 3) * FP.Pi * radius * radius * radius;
		var mass = volume * density;

		return new MassData {
			Mass = mass,
			Center = shape.Center,
			Inertia = FMatrix3.SphereInertia(mass, radius),
		};
	}

	/// <summary>Compute the bounding box of a transformed sphere.</summary>
	public static FAABB ComputeAABB(Sphere shape, FTransform transform) {
		var center = FTransform.TransformPoint(transform, shape.Center);
		var extent = new FVector3(shape.Radius, shape.Radius, shape.Radius);
		return new FAABB(center - extent, center + extent);
	}

	/// <summary>Overlap test between this sphere and a generic shape proxy.</summary>
	public static bool Overlap(Sphere shape, FTransform shapeTransform, ShapeProxy proxy) {
		var input = new DistanceInput {
			ProxyA = new ShapeProxy { Points = new[] { shape.Center }, Radius = shape.Radius },
			ProxyB = proxy,
			Transform = FTransform.Invert(shapeTransform),
			UseRadii = true,
		};

		var cache = SimplexCache.Empty;
		var output = Distance.ShapeDistance(input, ref cache);
		return output.Distance < B3Config.OverlapSlop;
	}

	/// <summary>Shape cast versus a sphere. Initial overlap is treated as a hit; see <see cref="Distance.ShapeCast"/>.</summary>
	public static CastOutput ShapeCast(Sphere shape, ShapeCastInput input) {
		var pairInput = new ShapeCastPairInput {
			ProxyA = new ShapeProxy { Points = new[] { shape.Center }, Radius = shape.Radius },
			ProxyB = input.Proxy,
			Transform = FTransform.Identity,
			TranslationB = input.Translation,
			MaxFraction = input.MaxFraction,
			CanEncroach = input.CanEncroach,
		};

		return Distance.ShapeCast(pairInput);
	}

	/// <summary>Compute the bounding box of a sphere swept between two transforms.</summary>
	public static FAABB ComputeSweptAABB(Sphere shape, FTransform xf1, FTransform xf2) {
		var r = new FVector3(shape.Radius, shape.Radius, shape.Radius);
		var center1 = FTransform.TransformPoint(xf1, shape.Center);
		var center2 = FTransform.TransformPoint(xf2, shape.Center);
		return new FAABB(FVector3.MinComponents(center1, center2) - r, FVector3.MaxComponents(center1, center2) + r);
	}

	/// <summary>
	/// Ray cast versus sphere in local space. A zero length ray is a point query. Initial overlap
	/// reports a hit at the ray origin with zero fraction and zero normal.
	/// </summary>
	public static CastOutput RayCast(Sphere shape, RayCastInput input) {
		var output = new CastOutput();

		var p = shape.Center;

		// Shift ray so the sphere center is the origin.
		var s = input.Origin - p;

		var r = shape.Radius;
		var rr = r * r;

		var d = FVector3.GetLengthAndNormalize(input.Translation, out var length);
		if (length == FP.Zero) {
			// Zero length ray.
			if (FVector3.LengthSqr(s) < rr) {
				output.Point = input.Origin;
				output.Hit = true;
			}

			return output;
		}

		// Find the closest point on the ray to the origin: solve dot(s + t * d, d) = 0.
		var t = -FVector3.Dot(s, d);
		var c = s + t * d;
		var cc = FVector3.Dot(c, c);

		if (cc > rr) {
			// Closest point is outside the sphere.
			return output;
		}

		// Pythagoras.
		var h = FP.Sqrt(rr - cc);
		var fraction = t - h;

		if (fraction < FP.Zero || input.MaxFraction * length < fraction) {
			// Intersection is outside the range of the ray segment.
			if (FVector3.LengthSqr(s) < rr) {
				output.Point = input.Origin;
				output.Hit = true;
			}

			return output;
		}

		var hitPoint = s + fraction * d;

		output.Fraction = fraction / length;
		if (output.Fraction > input.MaxFraction) {
			output.Fraction = input.MaxFraction;
		}

		output.Normal = FVector3.Normalize(hitPoint);
		output.Point = p + shape.Radius * output.Normal;
		output.Hit = true;

		return output;
	}

	/// <summary>
	/// Ray cast versus a hollow sphere shell in local space. Unlike the solid sphere a ray starting
	/// inside is not an overlap: it passes through and hits the far wall.
	/// </summary>
	public static CastOutput RayCastHollow(Sphere shape, RayCastInput input) {
		var output = new CastOutput();

		var p = shape.Center;
		var s = input.Origin - p;
		var d = FVector3.Normalize(input.Translation);

		var t = -FVector3.Dot(s, d);
		var c = s + t * d;
		var cc = FVector3.Dot(c, c);
		var r = shape.Radius;
		var rr = r * r;

		if (cc > rr) {
			return output;
		}

		var h = FP.Sqrt(rr - cc);
		var fraction = t - h;

		if (fraction < FP.Zero) {
			fraction = t + h;
		}

		if (fraction < FP.Zero) {
			// Behind the ray.
			return output;
		}

		if (fraction > input.MaxFraction) {
			return output;
		}

		var hitPoint = s + fraction * d;

		output.Fraction = fraction;
		output.Normal = FVector3.Normalize(hitPoint);
		output.Point = p + shape.Radius * output.Normal;
		output.Hit = true;

		return output;
	}
}
