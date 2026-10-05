// Derived from xpTURN Klotho 0.14.1 (FPNavMeshTriangle.cs), Apache-2.0.
// Modified for Space: Fixed64 math, immutable geometry, runtime attributes split into NavTriangleArea,
// derived fields computed by Create instead of stored by the baker.
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Immutable navmesh triangle: vertex indices, adjacency, funnel portal orientation, and centroid.
/// Neighbor <c>i</c> shares edge (<c>v[i]</c>, <c>v[(i + 1) % 3]</c>); <c>-1</c> marks a boundary edge.
/// </summary>
public readonly struct NavTriangle {
	public readonly int V0;
	public readonly int V1;
	public readonly int V2;
	public readonly int Neighbor0;
	public readonly int Neighbor1;
	public readonly int Neighbor2;

	/// <summary>
	/// Portal orientation, one bit per edge. Clear = (va, vb) is (left, right), set = flipped.
	/// Meaningless on boundary edges.
	/// </summary>
	public readonly byte PortalFlip;

	public readonly FVector2 CenterXZ;
	public readonly FP CenterY;

	private NavTriangle(int v0, int v1, int v2, int n0, int n1, int n2, byte portalFlip, FVector2 centerXZ, FP centerY) {
		V0 = v0;
		V1 = v1;
		V2 = v2;
		Neighbor0 = n0;
		Neighbor1 = n1;
		Neighbor2 = n2;
		PortalFlip = portalFlip;
		CenterXZ = centerXZ;
		CenterY = centerY;
	}

	/// <summary>
	/// Builds a triangle and derives its portal orientation and centroid from
	/// <paramref name="vertices"/>, so the stored fields can never disagree with the geometry.
	/// </summary>
	public static NavTriangle Create(ReadOnlySpan<FVector3> vertices, int v0, int v1, int v2, int neighbor0, int neighbor1, int neighbor2) {
		var a = vertices[v0];
		var b = vertices[v1];
		var c = vertices[v2];
		var centerXZ = (NavGeometry.ToXZ(a) + NavGeometry.ToXZ(b) + NavGeometry.ToXZ(c)) / 3;
		var centerY = (a.Y + b.Y + c.Y) / 3;

		byte portalFlip = 0;
		Span<int> neighbors = [neighbor0, neighbor1, neighbor2];
		Span<int> corners = [v0, v1, v2];
		for (var e = 0; e < 3; e++) {
			if (neighbors[e] < 0) {
				continue;
			}
			var edgeA = NavGeometry.ToXZ(vertices[corners[e]]);
			var edgeB = NavGeometry.ToXZ(vertices[corners[(e + 1) % 3]]);
			var opposite = NavGeometry.ToXZ(vertices[corners[(e + 2) % 3]]);
			var travelDirection = (edgeA + edgeB) * FP.Half - opposite;
			// Cross(travel, a - b) < 0 means a is on the funnel's left.
			if (FVector2.Cross(travelDirection, edgeA - edgeB) >= FP.Zero) {
				portalFlip |= (byte)(1 << e);
			}
		}

		return new NavTriangle(v0, v1, v2, neighbor0, neighbor1, neighbor2, portalFlip, centerXZ, centerY);
	}

	public int GetVertex(int corner) {
		return corner switch {
			0 => V0,
			1 => V1,
			2 => V2,
			_ => throw new ArgumentOutOfRangeException(nameof(corner)),
		};
	}

	public int GetNeighbor(int edge) {
		return edge switch {
			0 => Neighbor0,
			1 => Neighbor1,
			2 => Neighbor2,
			_ => throw new ArgumentOutOfRangeException(nameof(edge)),
		};
	}

	public void GetEdgeVertices(int edge, out int va, out int vb) {
		switch (edge) {
			case 0:
				va = V0;
				vb = V1;
				return;
			case 1:
				va = V1;
				vb = V2;
				return;
			case 2:
				va = V2;
				vb = V0;
				return;
			default:
				throw new ArgumentOutOfRangeException(nameof(edge));
		}
	}

	/// <summary>Portal vertex pair for an edge, or (-1, -1) on a boundary edge.</summary>
	public void GetPortal(int edge, out int left, out int right) {
		if (GetNeighbor(edge) < 0) {
			left = -1;
			right = -1;
			return;
		}
		GetEdgeVertices(edge, out var va, out var vb);
		if ((PortalFlip & (1 << edge)) != 0) {
			left = vb;
			right = va;
		} else {
			left = va;
			right = vb;
		}
	}
}

/// <summary>
/// Per-triangle attributes the simulation may rewrite at runtime (zones, doors). This is derived
/// state: it must be rewritten from snapshotted ECS state before pathfinding, never treated as
/// authoritative across a rollback.
/// </summary>
public struct NavTriangleArea {
	/// <summary>Area bits. A query may use the triangle when <c>(queryMask &amp; AreaMask) != 0</c>.</summary>
	public int AreaMask;

	/// <summary>A* edge cost multiplier. Must stay positive.</summary>
	public FP CostMultiplier;

	public bool IsBlocked;

	public static NavTriangleArea Default => new() { AreaMask = 1, CostMultiplier = FP.One };
}
