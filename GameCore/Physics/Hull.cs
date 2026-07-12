using Fixed32;

namespace Space.GameCore;

/// <summary>A hull face: its outward local-axis normal and the 4 corner indices of its loop (CCW viewed from outside).</summary>
public readonly struct HullFace {
	public readonly FVector3 Normal;
	public readonly int V0, V1, V2, V3;

	public HullFace(FVector3 normal, int v0, int v1, int v2, int v3) {
		Normal = normal;
		V0 = v0;
		V1 = v1;
		V2 = v2;
		V3 = v3;
	}
}

/// <summary>A hull edge: its two corner indices and the two face indices that share it.</summary>
public readonly struct HullEdge {
	public readonly int V0, V1, FaceA, FaceB;

	public HullEdge(int v0, int v1, int faceA, int faceB) {
		V0 = v0;
		V1 = v1;
		FaceA = faceA;
		FaceB = faceB;
	}
}

/// <summary>
/// A solid box, represented as an oriented box hull. box3d's b3Hull is a general convex polytope
/// (built via quickhull, with an arbitrary half-edge topology); this port only ever constructs
/// hulls via <see cref="MakeBox"/>, so it specializes directly to the box's fixed 6-face/12-edge/
/// 8-vertex topology rather than porting the general half-edge hull data structure. This lets
/// <see cref="Manifold"/>'s hull collision routines stay close to box3d's actual SAT + clipping
/// algorithm (convex_manifold.c) while working with concrete, hardcoded topology instead of
/// generic hull queries.
/// </summary>
public struct Hull {
	/// <summary>Local center.</summary>
	public FVector3 Center;

	/// <summary>Local rotation of the box's own axes.</summary>
	public FQuaternion Rotation;

	/// <summary>Half-width along each of the box's own (rotated) axes.</summary>
	public FVector3 HalfExtents;

	public Hull(FVector3 center, FQuaternion rotation, FVector3 halfExtents) {
		Center = center;
		Rotation = rotation;
		HalfExtents = halfExtents;
	}

	/// <summary>Builds an axis-aligned box hull.</summary>
	public static Hull MakeBox(FVector3 halfExtents, FVector3 center) {
		return new Hull(center, FQuaternion.Identity, halfExtents);
	}

	/// <summary>Builds a rotated box hull.</summary>
	public static Hull MakeBox(FVector3 halfExtents, FVector3 center, FQuaternion rotation) {
		return new Hull(center, rotation, halfExtents);
	}

	/// <summary>Compute mass properties of a box hull.</summary>
	public static MassData ComputeMass(Hull shape, FP density) {
		var h = shape.HalfExtents;
		var volume = 8 * h.X * h.Y * h.Z;
		var mass = volume * density;

		var localInertia = FMatrix3.BoxInertia(mass, -h, h);

		// Rotate the central inertia into the shape frame, as Capsule.ComputeMass does.
		var rotation = FMatrix3.FromQuaternion(shape.Rotation);
		return new MassData {
			Mass = mass,
			Center = shape.Center,
			Inertia = rotation * localInertia * FMatrix3.Transpose(rotation),
		};
	}

	/// <summary>Compute the bounding box of a transformed box hull.</summary>
	public static FAABB ComputeAABB(Hull shape, FTransform transform) {
		var center = FTransform.TransformPoint(transform, shape.Center);
		var rotation = transform.Rotation * shape.Rotation;
		var rotationMatrix = FMatrix3.FromQuaternion(rotation);
		var extent = FMatrix3.Abs(rotationMatrix) * shape.HalfExtents;
		return new FAABB(center - extent, center + extent);
	}

	/// <summary>The 8 corners of this box in shape-local space (already offset/rotated by <see cref="Center"/>/<see cref="Rotation"/>).</summary>
	public FVector3[] GetCorners() {
		var corners = new FVector3[8];
		WriteCorners(corners);
		return corners;
	}

	/// <summary>Non-allocating variant of <see cref="GetCorners"/> for hot paths that reuse a scratch buffer (<paramref name="dest"/> must have length &gt;= 8).</summary>
	public void WriteCorners(FVector3[] dest) {
		for (var i = 0; i < 8; i++) {
			dest[i] = Center + Rotation * LocalCorner(HalfExtents, i);
		}
	}

	/// <summary>
	/// Corner <paramref name="index"/> of a box with the given half-extents, in the box's own
	/// unrotated/uncentered (canonical) frame. Indices 0-3 loop the +Z face, 4-7 loop the -Z face
	/// at the same (X, Y) signs, matching <see cref="GetCorners"/>/<see cref="Edges"/>.
	/// </summary>
	public static FVector3 LocalCorner(FVector3 h, int index) {
		return index switch {
			0 => new FVector3(h.X, h.Y, h.Z),
			1 => new FVector3(-h.X, h.Y, h.Z),
			2 => new FVector3(-h.X, -h.Y, h.Z),
			3 => new FVector3(h.X, -h.Y, h.Z),
			4 => new FVector3(h.X, h.Y, -h.Z),
			5 => new FVector3(-h.X, h.Y, -h.Z),
			6 => new FVector3(-h.X, -h.Y, -h.Z),
			_ => new FVector3(h.X, -h.Y, -h.Z),
		};
	}

	/// <summary>
	/// Ray cast versus a box hull in local space (the frame <see cref="Center"/>/<see cref="Rotation"/>
	/// live in), via the standard slab method against the box's own rotated axes -- box3d's
	/// b3RayCastBox. A zero length ray is a point query. Initial overlap (ray origin already inside
	/// the box) reports a hit at the ray origin with zero fraction and zero normal, matching
	/// <see cref="Sphere.RayCast"/>/<see cref="Capsule.RayCast"/>'s convention for the same case.
	/// </summary>
	public static CastOutput RayCast(Hull shape, RayCastInput input) {
		var output = new CastOutput();

		var invRotation = FQuaternion.Inverse(shape.Rotation);
		var localOrigin = invRotation * (input.Origin - shape.Center);

		var d = FVector3.GetLengthAndNormalize(input.Translation, out var length);
		if (length == FP.Zero) {
			// Zero length ray: point containment.
			if (Contains(shape.HalfExtents, localOrigin)) {
				output.Point = input.Origin;
				output.Hit = true;
			}

			return output;
		}

		var localDirection = invRotation * d;

		var tMin = FP.Zero;
		var tMax = length * input.MaxFraction;
		var axis = -1;
		var sign = FP.One;

		for (var i = 0; i < 3; i++) {
			var start = Component(localOrigin, i);
			var dir = Component(localDirection, i);
			var extent = Component(shape.HalfExtents, i);

			if (FP.Abs(dir) < FP.CalculationsEpsilon) {
				if (start < -extent || start > extent) {
					// Parallel to this slab and outside it: no intersection possible.
					return output;
				}

				continue;
			}

			// Approaching from +axis (dir < 0) enters through the +extent face first (outward normal
			// +1 on this axis); approaching from -axis (dir > 0) enters through the -extent face
			// (outward normal -1). Assign near/far directly from the sign of dir instead of a
			// t1>t2 swap, so the entry-face sign can't get transposed from the entry distance.
			var invDir = FP.One / dir;
			var tToMin = (-extent - start) * invDir;
			var tToMax = (extent - start) * invDir;
			var nearT = dir > FP.Zero ? tToMin : tToMax;
			var farT = dir > FP.Zero ? tToMax : tToMin;
			var nearSign = dir > FP.Zero ? -FP.One : FP.One;

			if (nearT > tMin) {
				tMin = nearT;
				axis = i;
				sign = nearSign;
			}

			tMax = FP.Min(tMax, farT);

			if (tMin > tMax) {
				return output;
			}
		}

		if (axis < 0) {
			// The ray origin started inside every slab (tMin never advanced past zero).
			if (Contains(shape.HalfExtents, localOrigin)) {
				output.Point = input.Origin;
				output.Hit = true;
			}

			return output;
		}

		var localNormal = axis switch {
			0 => new FVector3(sign, FP.Zero, FP.Zero),
			1 => new FVector3(FP.Zero, sign, FP.Zero),
			_ => new FVector3(FP.Zero, FP.Zero, sign),
		};

		var localPoint = localOrigin + tMin * localDirection;

		output.Fraction = tMin / length;
		output.Normal = shape.Rotation * localNormal;
		output.Point = shape.Center + shape.Rotation * localPoint;
		output.Hit = true;
		return output;
	}

	private static bool Contains(FVector3 halfExtents, FVector3 point) {
		return FP.Abs(point.X) <= halfExtents.X && FP.Abs(point.Y) <= halfExtents.Y && FP.Abs(point.Z) <= halfExtents.Z;
	}

	private static FP Component(FVector3 v, int index) {
		return index switch {
			0 => v.X,
			1 => v.Y,
			_ => v.Z,
		};
	}

	/// <summary>The support corner of a box with half-extents <paramref name="h"/> along a local direction: the corner maximizing dot(direction, corner).</summary>
	public static FVector3 SupportLocal(FVector3 h, FVector3 direction) {
		return new FVector3(
			direction.X >= FP.Zero ? h.X : -h.X,
			direction.Y >= FP.Zero ? h.Y : -h.Y,
			direction.Z >= FP.Zero ? h.Z : -h.Z);
	}

	/// <summary>
	/// The 6 faces of a box hull, index-stable and referenced by <see cref="Edges"/>: 0=+X, 1=-X,
	/// 2=+Y, 3=-Y, 4=+Z, 5=-Z. Each loop is wound CCW viewed from outside the face.
	/// </summary>
	public static readonly HullFace[] Faces = {
		new(FVector3.Right, 0, 3, 7, 4),
		new(FVector3.Left, 1, 5, 6, 2),
		new(FVector3.Up, 0, 4, 5, 1),
		new(FVector3.Down, 3, 2, 6, 7),
		new(FVector3.Forward, 0, 1, 2, 3),
		new(FVector3.Backward, 4, 7, 6, 5),
	};

	/// <summary>The 12 edges of a box hull, each with the two face indices (into <see cref="Faces"/>) that share it.</summary>
	public static readonly HullEdge[] Edges = {
		new(0, 1, 4, 2),
		new(1, 2, 4, 1),
		new(2, 3, 4, 3),
		new(3, 0, 4, 0),
		new(4, 5, 5, 2),
		new(5, 6, 5, 1),
		new(6, 7, 5, 3),
		new(7, 4, 5, 0),
		new(0, 4, 0, 2),
		new(1, 5, 1, 2),
		new(2, 6, 1, 3),
		new(3, 7, 0, 3),
	};
}
