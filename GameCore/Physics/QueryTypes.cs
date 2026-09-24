using System;
using System.Runtime.CompilerServices;
using Fixed;
using Fixed32;

namespace Space.GameCore;

/// <summary>Controls whether world queries may report sensor shapes.</summary>
public enum QuerySensorMode : byte {
	Exclude,
	Include,
}

/// <summary>Inline point storage for <see cref="ShapeProxy"/>; only the first <see cref="ShapeProxy.Count"/> entries are valid.</summary>
[InlineArray(B3Config.MaxShapeCastPoints)]
public struct ShapeProxyPoints {
	private FVector3 _element0;
}

/// <summary>
/// A shape proxy used by the GJK algorithm. Can represent a convex shape as a point cloud with a radius.
/// Box3D's b3ShapeProxy points into shape-owned memory; this port stores the points inline instead so
/// building a proxy never allocates, which keeps queries and rollback resimulation free of GC pressure.
/// </summary>
public struct ShapeProxy {
	/// <summary>The point cloud. Only the first <see cref="Count"/> points are valid.</summary>
	public ShapeProxyPoints Points;

	/// <summary>The number of valid points, 1 to <see cref="B3Config.MaxShapeCastPoints"/>.</summary>
	public int Count;

	/// <summary>The external radius of the point cloud.</summary>
	public FP Radius;

	public ShapeProxy(ReadOnlySpan<FVector3> points, FP radius) {
		if (points.Length > B3Config.MaxShapeCastPoints) {
			throw new ArgumentException($"A shape proxy supports at most {B3Config.MaxShapeCastPoints} points.", nameof(points));
		}
		Points = default;
		for (var i = 0; i < points.Length; i++) {
			Points[i] = points[i];
		}
		Count = points.Length;
		Radius = radius;
	}

	public static ShapeProxy MakePoint(FVector3 point, FP radius) {
		var proxy = new ShapeProxy { Count = 1, Radius = radius };
		proxy.Points[0] = point;
		return proxy;
	}

	public static ShapeProxy MakeSegment(FVector3 point1, FVector3 point2, FP radius) {
		var proxy = new ShapeProxy { Count = 2, Radius = radius };
		proxy.Points[0] = point1;
		proxy.Points[1] = point2;
		return proxy;
	}
}

/// <summary>Outcome of a continuous, rotating convex time-of-impact query.</summary>
public enum TimeOfImpactState : byte {
	/// <summary>The query did not produce a result.</summary>
	Unknown,

	/// <summary>The deterministic iteration limit or fixed-point progress limit was reached.</summary>
	Failed,

	/// <summary>The proxy point clouds overlap at fraction zero.</summary>
	Overlapped,

	/// <summary>The shapes reach the time-of-impact target within <see cref="B3Config.LinearSlop"/>.</summary>
	Hit,

	/// <summary>The shapes remain separated through the requested maximum fraction.</summary>
	Separated,
}

/// <summary>
/// Input for a deterministic continuous collision query between two rotating convex proxies.
/// Translation is interpolated in 64-bit world space and rotation uses shortest-path
/// <see cref="FQuaternion.Nlerp(FQuaternion, FQuaternion, FP, bool)"/>.
/// </summary>
public struct TimeOfImpactInput {
	/// <summary>The convex proxy for shape A.</summary>
	public ShapeProxy ProxyA;

	/// <summary>The convex proxy for shape B.</summary>
	public ShapeProxy ProxyB;

	/// <summary>World transform of shape A at fraction zero.</summary>
	public FWorldTransform TransformAStart;

	/// <summary>World transform of shape A at fraction one.</summary>
	public FWorldTransform TransformAEnd;

	/// <summary>Shape A's local center of rotation. Zero for an ordinary transform sweep.</summary>
	public FVector3 LocalCenterA;

	/// <summary>World transform of shape B at fraction zero.</summary>
	public FWorldTransform TransformBStart;

	/// <summary>World transform of shape B at fraction one.</summary>
	public FWorldTransform TransformBEnd;

	/// <summary>Shape B's local center of rotation. Zero for an ordinary transform sweep.</summary>
	public FVector3 LocalCenterB;

	/// <summary>The inclusive sweep fraction to consider, clamped to the range [0, 1].</summary>
	public FP MaxFraction;
}

/// <summary>Result of a rotating convex time-of-impact query.</summary>
public struct TimeOfImpactOutput {
	/// <summary>The way the query terminated.</summary>
	public TimeOfImpactState State;

	/// <summary>
	/// Fraction along both start-to-end sweeps. For <see cref="TimeOfImpactState.Separated"/> this
	/// is the requested maximum fraction; for <see cref="TimeOfImpactState.Failed"/> it is the last
	/// conservatively reached fraction.
	/// </summary>
	public FP Fraction;

	/// <summary>
	/// World-space contact estimate, averaged between the radius-adjusted GJK witness points at
	/// <see cref="Fraction"/>. This retains the 64-bit precision of world positions.
	/// </summary>
	public FPos Point;

	/// <summary>Normalized world-space normal pointing from shape A toward shape B.</summary>
	public FVector3 Normal;
}

/// <summary>
/// Low level ray cast input data.
/// </summary>
public struct RayCastInput {
	/// <summary>Start point of the ray cast.</summary>
	public FVector3 Origin;

	/// <summary>Translation of the ray cast. end = start + translation.</summary>
	public FVector3 Translation;

	/// <summary>The maximum fraction of the translation to consider, typically 1.</summary>
	public FP MaxFraction;
}

/// <summary>
/// Low level shape cast input in generic form. Allows casting an arbitrary point cloud wrapped
/// with a radius (e.g. a sphere is a single point with a non-zero radius).
/// </summary>
public struct ShapeCastInput {
	/// <summary>A generic query shape.</summary>
	public ShapeProxy Proxy;

	/// <summary>The translation of the shape cast.</summary>
	public FVector3 Translation;

	/// <summary>The maximum fraction of the translation to consider, typically 1.</summary>
	public FP MaxFraction;

	/// <summary>Allow shape cast to encroach when initially touching. Only works if the radius is greater than zero.</summary>
	public bool CanEncroach;
}

/// <summary>
/// Low level ray cast or shape-cast output data.
/// </summary>
public struct CastOutput {
	/// <summary>The surface normal at the hit point.</summary>
	public FVector3 Normal;

	/// <summary>The surface hit point.</summary>
	public FVector3 Point;

	/// <summary>The fraction of the input translation at collision.</summary>
	public FP Fraction;

	/// <summary>The number of iterations used.</summary>
	public int Iterations;

	/// <summary>The index of the mesh or height field triangle hit.</summary>
	public int TriangleIndex;

	/// <summary>The index of the compound child shape.</summary>
	public int ChildIndex;

	/// <summary>The material index. May be -1 for null.</summary>
	public int MaterialIndex;

	/// <summary>Did the cast hit?</summary>
	public bool Hit;
}

/// <summary>
/// Used to warm start the GJK simplex. If you call this multiple times with nearby transforms
/// this might improve performance. Otherwise zero-initialize (the default).
/// </summary>
public struct SimplexCache {
	/// <summary>Value used to compare length, area, volume of two simplexes.</summary>
	public FP Metric;

	/// <summary>The number of stored simplex points.</summary>
	public ushort Count;

	/// <summary>The cached simplex indices on shape A.</summary>
	public SimplexIndices IndexA;

	/// <summary>The cached simplex indices on shape B.</summary>
	public SimplexIndices IndexB;

	public static SimplexCache Empty => default;
}

/// <summary>Inline storage for the four cached simplex vertex indices of one shape.</summary>
[InlineArray(4)]
public struct SimplexIndices {
	private byte _element0;
}

/// <summary>
/// Input parameters for a pairwise shape cast.
/// </summary>
public struct ShapeCastPairInput {
	/// <summary>The proxy for shape A.</summary>
	public ShapeProxy ProxyA;

	/// <summary>The proxy for shape B.</summary>
	public ShapeProxy ProxyB;

	/// <summary>Transform of shape B in shape A's frame, the relative pose B in A.</summary>
	public FTransform Transform;

	/// <summary>The translation of shape B, in A's frame.</summary>
	public FVector3 TranslationB;

	/// <summary>The fraction of the translation to consider, typically 1.</summary>
	public FP MaxFraction;

	/// <summary>Allows shapes with a radius to move slightly closer if already touching.</summary>
	public bool CanEncroach;
}

/// <summary>
/// Input for a shape distance query.
/// </summary>
public struct DistanceInput {
	/// <summary>The proxy for shape A.</summary>
	public ShapeProxy ProxyA;

	/// <summary>The proxy for shape B.</summary>
	public ShapeProxy ProxyB;

	/// <summary>
	/// Transform of shape B in shape A's frame, the relative pose B in A. The query is origin
	/// independent and runs in frame A.
	/// </summary>
	public FTransform Transform;

	/// <summary>Should the proxy radius be considered?</summary>
	public bool UseRadii;
}

/// <summary>
/// Output for a shape distance query.
/// </summary>
public struct DistanceOutput {
	/// <summary>Closest point on shape A, in shape A's frame.</summary>
	public FVector3 PointA;

	/// <summary>Closest point on shape B, in shape A's frame.</summary>
	public FVector3 PointB;

	/// <summary>A to B normal in shape A's frame. Invalid if distance is zero.</summary>
	public FVector3 Normal;

	/// <summary>The final distance, zero if overlapped.</summary>
	public FP Distance;

	/// <summary>Number of GJK iterations used.</summary>
	public int Iterations;

	/// <summary>The number of simplexes stored in the simplex array.</summary>
	public int SimplexCount;
}

/// <summary>
/// Simplex vertex for debugging the GJK algorithm.
/// </summary>
public struct SimplexVertex {
	/// <summary>Support point in proxy A.</summary>
	public FVector3 WA;

	/// <summary>Support point in proxy B.</summary>
	public FVector3 WB;

	/// <summary>wB - wA.</summary>
	public FVector3 W;

	/// <summary>Barycentric coordinate.</summary>
	public FP A;

	/// <summary>wA index.</summary>
	public int IndexA;

	/// <summary>wB index.</summary>
	public int IndexB;
}

/// <summary>
/// Simplex from the GJK algorithm.
/// </summary>
public struct Simplex {
	/// <summary>Vertex 0. Prefer the indexer for generic access; only the first <see cref="Count"/> are valid.</summary>
	public SimplexVertex V0;

	/// <summary>Vertex 1.</summary>
	public SimplexVertex V1;

	/// <summary>Vertex 2.</summary>
	public SimplexVertex V2;

	/// <summary>Vertex 3.</summary>
	public SimplexVertex V3;

	/// <summary>Number of valid vertices.</summary>
	public int Count;

	/// <summary>
	/// Indexed access to the four vertex slots by reference, so callers can mutate a field in
	/// place (<c>Simplex.VertexAt(ref simplex, 0).A = value</c>), matching how box3d indexes its
	/// embedded-by-value vertex array. C# does not allow a struct instance member to ref-return one
	/// of its own fields (the instance could be a temporary copy), hence the explicit ref parameter.
	/// All four fields are plain value types, so unlike an array-backed design, copying a Simplex
	/// (e.g. for a GJK backup/restore) is always a true, independent deep copy.
	/// </summary>
	public static ref SimplexVertex VertexAt(ref Simplex simplex, int index) {
		switch (index) {
			case 0:
				return ref simplex.V0;
			case 1:
				return ref simplex.V1;
			case 2:
				return ref simplex.V2;
			case 3:
				return ref simplex.V3;
			default:
				throw new IndexOutOfRangeException();
		}
	}

	public static Simplex Empty => default;
}
