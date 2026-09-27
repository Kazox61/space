// Derived from xpTURN Klotho 0.14.1 (FPNavMeshQuery.cs, math primitives), Apache-2.0.
// Modified for Space: Fixed64 math, epsilons built from integer ratios.
using Fixed64;

namespace Space.GameCore;

/// <summary>2D (XZ-plane) geometry primitives shared by the navigation runtime.</summary>
public static class NavGeometry {
	/// <summary>Tolerance for point-in-triangle tests, so points on shared edges belong to both sides.</summary>
	public static readonly FP PointInTriangleEpsilon = FP.FromRatio(1, 10000);

	private static readonly FP s_barycentricDenominatorEpsilon = FP.FromRatio(1, 10000);

	public static FVector2 ToXZ(FVector3 v) {
		return new FVector2(v.X, v.Z);
	}

	/// <summary>
	/// Point-in-triangle by cross-product sign, tolerant by <see cref="PointInTriangleEpsilon"/>
	/// and independent of winding.
	/// </summary>
	public static bool PointInTriangle(FVector2 p, FVector2 a, FVector2 b, FVector2 c) {
		var d1 = FVector2.Cross(b - a, p - a);
		var d2 = FVector2.Cross(c - b, p - b);
		var d3 = FVector2.Cross(a - c, p - c);
		var epsilon = PointInTriangleEpsilon;
		var hasNegative = d1 < -epsilon || d2 < -epsilon || d3 < -epsilon;
		var hasPositive = d1 > epsilon || d2 > epsilon || d3 > epsilon;
		return !(hasNegative && hasPositive);
	}

	/// <summary>
	/// Barycentric weights of <paramref name="p"/>. Degenerate triangles fall back to equal weights.
	/// </summary>
	public static void Barycentric(FVector2 p, FVector2 a, FVector2 b, FVector2 c, out FP u, out FP v, out FP w) {
		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;
		var d00 = FVector2.Dot(v0, v0);
		var d01 = FVector2.Dot(v0, v1);
		var d11 = FVector2.Dot(v1, v1);
		var d20 = FVector2.Dot(v2, v0);
		var d21 = FVector2.Dot(v2, v1);
		var denominator = d00 * d11 - d01 * d01;

		if (FP.Abs(denominator) < s_barycentricDenominatorEpsilon) {
			u = v = w = FP.FromRatio(1, 3);
			return;
		}

		v = (d11 * d20 - d01 * d21) / denominator;
		w = (d00 * d21 - d01 * d20) / denominator;
		u = FP.One - v - w;
	}

	public static FVector2 ClosestPointOnSegment(FVector2 p, FVector2 a, FVector2 b) {
		var ab = b - a;
		var lengthSqr = FVector2.LengthSqr(ab);
		if (lengthSqr == FP.Zero) {
			return a;
		}
		var t = FP.Clamp01(FVector2.Dot(p - a, ab) / lengthSqr);
		return a + ab * t;
	}

	/// <summary>Signed area: positive for counter-clockwise winding in (X, Z).</summary>
	public static FP SignedArea(FVector2 a, FVector2 b, FVector2 c) {
		return FVector2.Cross(b - a, c - a) * FP.Half;
	}
}
