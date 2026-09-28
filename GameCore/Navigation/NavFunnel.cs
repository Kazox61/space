// Derived from xpTURN Klotho 0.14.1 (FPNavMeshFunnel.cs), Apache-2.0.
// Modified for Space: Fixed64 math, caller-owned output buffers, bit-exact apex comparison via
// FVector2.Equals (Space's == is approximate), shared trace loop for paths and corners.
using System.Diagnostics;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Simple stupid funnel algorithm: turns a triangle corridor into string-pulled waypoints.
/// </summary>
public sealed class NavFunnel {
	private static readonly FP s_epsilon = FP.FromRatio(1, 10000);
	private static readonly FP s_minCornerDistanceSqr = FP.FromRatio(1, 10000);

	private readonly NavMesh _mesh;
	private readonly FVector3[] _portalLeft;
	private readonly FVector3[] _portalRight;

	public NavFunnel(NavMesh mesh, NavConfig config) {
		ArgumentNullException.ThrowIfNull(mesh);
		_mesh = mesh;
		var capacity = Math.Max(config.MaxPortals, NavAgent.CorridorCapacity + 2);
		_portalLeft = new FVector3[capacity];
		_portalRight = new FVector3[capacity];
	}

	/// <summary>
	/// Full path: <paramref name="start"/>, the funnel corners, then <paramref name="end"/>.
	/// Returns the waypoint count. When <paramref name="waypoints"/> is too small, trailing corners
	/// are dropped but the end is kept.
	/// </summary>
	public int FindPath(ReadOnlySpan<int> corridor, FVector3 start, FVector3 end, Span<FVector3> waypoints) {
		ValidateCorridorCapacity(corridor);
		if (corridor.IsEmpty || waypoints.IsEmpty) {
			return 0;
		}
		waypoints[0] = start;
		if (waypoints.Length == 1) {
			return 1;
		}
		if (corridor.Length == 1) {
			waypoints[1] = end;
			return 2;
		}

		var portalCount = BuildPortals(corridor, start, end);
		var count = 1 + TraceCorners(portalCount, start, waypoints[1..^1]);
		waypoints[count++] = end;
		return count;
	}

	/// <summary>
	/// The next corners to steer through from <paramref name="position"/>, ending with
	/// <paramref name="target"/> when it fits. Corners within 0.01 of the position are skipped.
	/// <paramref name="corridor"/> must start at the triangle containing <paramref name="position"/>.
	/// </summary>
	public int FindCorners(ReadOnlySpan<int> corridor, FVector3 position, FVector3 target, Span<FVector3> corners) {
		ValidateCorridorCapacity(corridor);
		if (corridor.IsEmpty || corners.IsEmpty) {
			return 0;
		}
		if (corridor.Length == 1) {
			corners[0] = target;
			return 1;
		}

		var portalCount = BuildPortals(corridor, position, target);
		var count = TraceCorners(portalCount, position, corners);
		if (count < corners.Length) {
			corners[count++] = target;
		}
		return PruneNearCorners(position, corners[..count]);
	}

	/// <summary>
	/// Portals for the corridor: the start point, each shared edge, then the end point.
	/// </summary>
	private int BuildPortals(ReadOnlySpan<int> corridor, FVector3 start, FVector3 end) {
		_portalLeft[0] = start;
		_portalRight[0] = start;
		var count = 1;
		for (var i = 0; i < corridor.Length - 1; i++) {
			GetSharedPortal(corridor[i], corridor[i + 1], out _portalLeft[count], out _portalRight[count]);
			count++;
		}
		_portalLeft[count] = end;
		_portalRight[count] = end;
		return count + 1;
	}

	private void ValidateCorridorCapacity(ReadOnlySpan<int> corridor) {
		if (corridor.Length >= _portalLeft.Length) {
			throw new ArgumentException($"Corridor of {corridor.Length} triangles needs {corridor.Length + 1} portals; capacity is {_portalLeft.Length}.", nameof(corridor));
		}
	}

	private void GetSharedPortal(int triangle, int next, out FVector3 left, out FVector3 right) {
		ref readonly var t = ref _mesh.Triangles[triangle];
		left = right = default;
		var found = false;
		for (var e = 0; e < 3; e++) {
			if (t.GetNeighbor(e) == next) {
				t.GetPortal(e, out var leftIndex, out var rightIndex);
				left = _mesh.Vertices[leftIndex];
				right = _mesh.Vertices[rightIndex];
				found = true;
				break;
			}
		}
		Debug.Assert(found, $"Corridor triangles {triangle} and {next} do not share an edge.");
		if (found) {
			return;
		}
		// Not adjacent: a malformed corridor. Collapse the portal to the centroid rather than fail.
		left = right = new FVector3(t.CenterXZ.X, t.CenterY, t.CenterXZ.Y);
	}

	/// <summary>
	/// Traces the funnel over portals [1, portalCount) and writes the apex corners it produces,
	/// stopping when <paramref name="output"/> is full. The end point itself is not written.
	/// </summary>
	private int TraceCorners(int portalCount, FVector3 start, Span<FVector3> output) {
		var count = 0;
		var apex = NavGeometry.ToXZ(start);
		var funnelLeft = apex;
		var funnelRight = apex;
		var leftIndex = 0;
		var rightIndex = 0;

		for (var i = 1; i < portalCount && count < output.Length; i++) {
			var newLeft = NavGeometry.ToXZ(_portalLeft[i]);
			var newRight = NavGeometry.ToXZ(_portalRight[i]);

			// Tighten the right side.
			if (FVector2.Cross(funnelRight - apex, newRight - apex) <= s_epsilon) {
				// Exact equality: the apex is always copied from a portal, never computed.
				if (apex.Equals(funnelRight) || FVector2.Cross(funnelLeft - apex, newRight - apex) > -s_epsilon) {
					funnelRight = newRight;
					rightIndex = i;
				} else {
					// The right crossed over the left: the left point becomes a corner.
					apex = funnelLeft;
					output[count++] = new FVector3(apex.X, _portalLeft[leftIndex].Y, apex.Y);
					funnelRight = apex;
					rightIndex = leftIndex;
					i = leftIndex;
					continue;
				}
			}

			// Tighten the left side.
			if (FVector2.Cross(funnelLeft - apex, newLeft - apex) >= -s_epsilon) {
				if (apex.Equals(funnelLeft) || FVector2.Cross(funnelRight - apex, newLeft - apex) < s_epsilon) {
					funnelLeft = newLeft;
					leftIndex = i;
				} else {
					apex = funnelRight;
					output[count++] = new FVector3(apex.X, _portalRight[rightIndex].Y, apex.Y);
					funnelLeft = apex;
					leftIndex = rightIndex;
					i = rightIndex;
					continue;
				}
			}
		}
		return count;
	}

	private static int PruneNearCorners(FVector3 position, Span<FVector3> corners) {
		var positionXZ = NavGeometry.ToXZ(position);
		var skip = 0;
		while (skip < corners.Length - 1 && FVector2.DistanceSqr(positionXZ, NavGeometry.ToXZ(corners[skip])) <= s_minCornerDistanceSqr) {
			skip++;
		}
		if (skip > 0) {
			corners[skip..].CopyTo(corners);
		}
		return corners.Length - skip;
	}
}
