// Derived from xpTURN Klotho 0.14.1 (FPNavMeshQuery.cs), Apache-2.0.
// Modified for Space: Fixed64 math, logging removed, surface walk and raycast not ported,
// lookups clamp to the covered grid, nearest-point results are always found by the lookup.
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Point queries against a <see cref="NavMesh"/>: triangle lookup, height sampling, and
/// nearest-point search. Holds per-call scratch only; results depend solely on the mesh and
/// the arguments.
/// </summary>
public sealed class NavMeshQuery {
	private readonly NavMesh _mesh;
	private readonly int[] _visited;
	private int _generation;

	public NavMeshQuery(NavMesh mesh) {
		ArgumentNullException.ThrowIfNull(mesh);
		_mesh = mesh;
		_visited = new int[mesh.TriangleCount];
	}

	public NavMesh Mesh => _mesh;

	/// <summary>Whether a query carrying <paramref name="areaMask"/> may occupy the triangle.</summary>
	public bool IsPassable(int triangle, int areaMask) {
		ref readonly var area = ref _mesh.Areas[triangle];
		return !area.IsBlocked && (areaMask & area.AreaMask) != 0;
	}

	/// <summary>First triangle (ascending index within the cell) containing the point, or -1.</summary>
	public int FindTriangle(FVector2 xz) {
		if (!_mesh.TryGetContainingCell(xz, out var col, out var row)) {
			return -1;
		}
		foreach (var triangle in _mesh.GetCellTriangles(col, row)) {
			if (Contains(triangle, xz)) {
				return triangle;
			}
		}
		return -1;
	}

	/// <summary>
	/// Containing triangle whose surface is nearest <paramref name="y"/>, for overlapping floors.
	/// Ties keep the lower triangle index.
	/// </summary>
	public int FindTriangle(FVector2 xz, FP y) {
		if (!_mesh.TryGetContainingCell(xz, out var col, out var row)) {
			return -1;
		}
		var best = -1;
		var bestDistance = FP.MaxValue;
		foreach (var triangle in _mesh.GetCellTriangles(col, row)) {
			if (!Contains(triangle, xz)) {
				continue;
			}
			var distance = FP.Abs(SampleHeight(xz, triangle) - y);
			if (distance < bestDistance) {
				bestDistance = distance;
				best = triangle;
			}
		}
		return best;
	}

	/// <summary>
	/// <see cref="FindTriangle(FVector2, FP)"/> for path endpoints: when candidates lie on the same
	/// surface (a point on a shared edge), a triangle passable for <paramref name="tieBreakMask"/>
	/// wins over one that is not. Different floors are never swapped, even at equal distance.
	/// </summary>
	public int FindTriangleForEndpoint(FVector2 xz, FP y, int tieBreakMask) {
		if (!_mesh.TryGetContainingCell(xz, out var col, out var row)) {
			return -1;
		}
		var best = -1;
		var bestDistance = FP.MaxValue;
		var bestSurfaceY = FP.Zero;
		var bestPassable = false;
		foreach (var triangle in _mesh.GetCellTriangles(col, row)) {
			if (!Contains(triangle, xz)) {
				continue;
			}
			var surfaceY = SampleHeight(xz, triangle);
			var distance = FP.Abs(surfaceY - y);
			var passable = IsPassable(triangle, tieBreakMask);
			var sameSurface = best >= 0 && FP.Abs(surfaceY - bestSurfaceY) <= NavGeometry.PointInTriangleEpsilon;
			if (best < 0 || (!sameSurface && distance < bestDistance)) {
				bestDistance = distance;
				bestSurfaceY = surfaceY;
				best = triangle;
				bestPassable = passable;
			} else if (sameSurface) {
				if (distance < bestDistance) {
					bestDistance = distance;
					bestSurfaceY = surfaceY;
				}
				if (passable && !bestPassable) {
					best = triangle;
					bestPassable = true;
				}
			}
		}
		return best;
	}

	/// <summary>
	/// <see cref="FindTriangleForEndpoint"/>, returning -1 unless the chosen triangle is passable.
	/// Use this to test whether a destination is usable as given.
	/// </summary>
	public int FindPassableTriangleForEndpoint(FVector2 xz, FP y, int areaMask) {
		var triangle = FindTriangleForEndpoint(xz, y, areaMask);
		return triangle >= 0 && IsPassable(triangle, areaMask) ? triangle : -1;
	}

	/// <summary>First passable triangle containing the point, or -1. Height-blind.</summary>
	public int FindPassableTriangle(FVector2 xz, int areaMask) {
		if (!_mesh.TryGetContainingCell(xz, out var col, out var row)) {
			return -1;
		}
		foreach (var triangle in _mesh.GetCellTriangles(col, row)) {
			if (IsPassable(triangle, areaMask) && Contains(triangle, xz)) {
				return triangle;
			}
		}
		return -1;
	}

	/// <summary>
	/// Surface height at <paramref name="xz"/> by barycentric interpolation, clamped to the
	/// triangle's vertex height range.
	/// </summary>
	public FP SampleHeight(FVector2 xz, int triangle) {
		ref readonly var t = ref _mesh.Triangles[triangle];
		var a = _mesh.Vertices[t.V0];
		var b = _mesh.Vertices[t.V1];
		var c = _mesh.Vertices[t.V2];
		NavGeometry.Barycentric(xz, NavGeometry.ToXZ(a), NavGeometry.ToXZ(b), NavGeometry.ToXZ(c), out var u, out var v, out var w);
		var height = a.Y * u + b.Y * v + c.Y * w;
		var minY = FP.Min(FP.Min(a.Y, b.Y), c.Y);
		var maxY = FP.Max(FP.Max(a.Y, b.Y), c.Y);
		return FP.Clamp(height, minY, maxY);
	}

	/// <summary>
	/// The point itself when <see cref="FindTriangle(FVector2)"/> finds it, otherwise the nearest
	/// triangle-edge point on the mesh. <paramref name="triangle"/> is -1 when
	/// nothing is found. A returned point is always found again by <see cref="FindTriangle(FVector2)"/>.
	/// Ignores blocking and area masks.
	/// </summary>
	public FVector2 ClosestPoint(FVector2 xz, out int triangle) {
		return ClosestPointCore(xz, filter: false, areaMask: 0, 0, _mesh.GridWidth - 1, 0, _mesh.GridHeight - 1, out triangle);
	}

	/// <summary><see cref="ClosestPoint"/> restricted to triangles passable for <paramref name="areaMask"/>.</summary>
	public FVector2 ClosestPassablePoint(FVector2 xz, int areaMask, out int triangle) {
		return ClosestPointCore(xz, filter: true, areaMask, 0, _mesh.GridWidth - 1, 0, _mesh.GridHeight - 1, out triangle);
	}

	/// <summary>
	/// Snaps to the mesh within <paramref name="maxDistance"/>. On failure returns
	/// <paramref name="xz"/> unchanged and sets <paramref name="triangle"/> to -1.
	/// </summary>
	public FVector2 Project(FVector2 xz, FP maxDistance, out int triangle) {
		return ProjectCore(xz, maxDistance, filter: false, areaMask: 0, out triangle);
	}

	/// <summary><see cref="Project"/> restricted to triangles passable for <paramref name="areaMask"/>.</summary>
	public FVector2 ProjectToPassable(FVector2 xz, FP maxDistance, int areaMask, out int triangle) {
		return ProjectCore(xz, maxDistance, filter: true, areaMask, targetY: null, out triangle);
	}

	/// <summary>
	/// <see cref="ProjectToPassable(FVector2, FP, int, out int)"/> with the requested height included
	/// in candidate ranking, so overlapping floors do not resolve by triangle order.
	/// </summary>
	public FVector2 ProjectToPassable(FVector2 xz, FP y, FP maxDistance, int areaMask, out int triangle) {
		return ProjectCore(xz, maxDistance, filter: true, areaMask, y, out triangle);
	}

	private FVector2 ProjectCore(FVector2 xz, FP maxDistance, bool filter, int areaMask, out int triangle) {
		return ProjectCore(xz, maxDistance, filter, areaMask, targetY: null, out triangle);
	}

	private FVector2 ProjectCore(FVector2 xz, FP maxDistance, bool filter, int areaMask, FP? targetY, out int triangle) {
		if (maxDistance < FP.Zero) {
			throw new ArgumentOutOfRangeException(nameof(maxDistance), "Must not be negative.");
		}
		GetCellRange(xz.X, _mesh.GridOrigin.X, maxDistance, _mesh.GridWidth, out var minCol, out var maxCol);
		GetCellRange(xz.Y, _mesh.GridOrigin.Y, maxDistance, _mesh.GridHeight, out var minRow, out var maxRow);
		var closest = ClosestPointCore(xz, filter, areaMask, minCol, maxCol, minRow, maxRow, out triangle, targetY);
		if (triangle < 0) {
			return xz;
		}
		if (!WithinDistance(xz, closest, maxDistance)) {
			triangle = -1;
			return xz;
		}
		return closest;
	}

	private static bool WithinDistance(FVector2 a, FVector2 b, FP maxDistance) {
		var dx = (Int128)a.X.RawValue - b.X.RawValue;
		var dy = (Int128)a.Y.RawValue - b.Y.RawValue;
		dx = dx < 0 ? -dx : dx;
		dy = dy < 0 ? -dy : dy;
		var limit = (Int128)maxDistance.RawValue;
		return dx <= limit && dy <= limit && dx * dx + dy * dy <= limit * limit;
	}

	private void GetCellRange(FP coordinate, FP origin, FP maxDistance, int cellCount, out int min, out int max) {
		var cellSize = (Int128)_mesh.GridCellSize.RawValue;
		var relative = (Int128)coordinate.RawValue - origin.RawValue;
		// Include one conservative cell on each side because integer division truncates toward zero.
		var first = (relative - maxDistance.RawValue) / cellSize - 1;
		var last = (relative + maxDistance.RawValue) / cellSize + 1;
		min = first <= 0 ? 0 : first >= cellCount ? cellCount : (int)first;
		max = last < 0 ? -1 : last >= cellCount ? cellCount - 1 : (int)last;
	}

	private FVector2 ClosestPointCore(FVector2 xz, bool filter, int areaMask, int minCol, int maxCol, int minRow, int maxRow, out int triangle, FP? targetY = null) {
		triangle = targetY.HasValue ? -1 : filter ? FindPassableTriangle(xz, areaMask) : FindTriangle(xz);
		if (triangle >= 0) {
			return xz;
		}

		NextGeneration();

		var bestDistanceSqr = FP.MaxValue;
		var bestDistanceSqr3D = UInt128.MaxValue;
		var bestPoint = xz;
		var bestTriangle = -1;
		for (var row = minRow; row <= maxRow; row++) {
			for (var col = minCol; col <= maxCol; col++) {
				foreach (var candidate in _mesh.GetCellTriangles(col, row)) {
					if (_visited[candidate] == _generation) {
						continue;
					}
					_visited[candidate] = _generation;
					if (filter && !IsPassable(candidate, areaMask)) {
						continue;
					}

					// No point-in-triangle shortcut here: the lookup above already missed the point, so a
					// triangle accepting it within tolerance lies in another cell or past the bounds, where
					// a lookup of the same point would miss it again. Its nearest edge point is inside the
					// triangle's bounding box, which the grid covers, so the result can always be found.
					ref readonly var t = ref _mesh.Triangles[candidate];
					var a = _mesh.GetVertexXZ(t.V0);
					var b = _mesh.GetVertexXZ(t.V1);
					var c = _mesh.GetVertexXZ(t.V2);
					if (targetY.HasValue) {
						if (Contains(candidate, xz)) {
							CheckPoint3D(xz, targetY.Value, xz, candidate, ref bestDistanceSqr3D, ref bestPoint, ref bestTriangle);
						}
						CheckEdge3D(xz, targetY.Value, a, b, candidate, ref bestDistanceSqr3D, ref bestPoint, ref bestTriangle);
						CheckEdge3D(xz, targetY.Value, b, c, candidate, ref bestDistanceSqr3D, ref bestPoint, ref bestTriangle);
						CheckEdge3D(xz, targetY.Value, c, a, candidate, ref bestDistanceSqr3D, ref bestPoint, ref bestTriangle);
					} else {
						CheckEdge(xz, a, b, candidate, ref bestDistanceSqr, ref bestPoint, ref bestTriangle);
						CheckEdge(xz, b, c, candidate, ref bestDistanceSqr, ref bestPoint, ref bestTriangle);
						CheckEdge(xz, c, a, candidate, ref bestDistanceSqr, ref bestPoint, ref bestTriangle);
					}
				}
			}
		}

		triangle = bestTriangle;
		return bestPoint;
	}

	private bool Contains(int triangle, FVector2 xz) {
		ref readonly var t = ref _mesh.Triangles[triangle];
		return NavGeometry.PointInTriangle(xz, _mesh.GetVertexXZ(t.V0), _mesh.GetVertexXZ(t.V1), _mesh.GetVertexXZ(t.V2));
	}

	private void NextGeneration() {
		_generation++;
		if (_generation == int.MaxValue) {
			// 0 is the never-visited sentinel, so wrap to 1.
			Array.Clear(_visited);
			_generation = 1;
		}
	}

	private static void CheckEdge(FVector2 p, FVector2 a, FVector2 b, int triangle, ref FP bestDistanceSqr, ref FVector2 bestPoint, ref int bestTriangle) {
		var closest = NavGeometry.ClosestPointOnSegment(p, a, b);
		var distanceSqr = FVector2.DistanceSqr(p, closest);
		if (distanceSqr < bestDistanceSqr) {
			bestDistanceSqr = distanceSqr;
			bestPoint = closest;
			bestTriangle = triangle;
		}
	}

	private void CheckEdge3D(FVector2 p, FP y, FVector2 a, FVector2 b, int triangle, ref UInt128 bestDistanceSqr, ref FVector2 bestPoint, ref int bestTriangle) {
		var closest = NavGeometry.ClosestPointOnSegment(p, a, b);
		CheckPoint3D(p, y, closest, triangle, ref bestDistanceSqr, ref bestPoint, ref bestTriangle);
	}

	private void CheckPoint3D(FVector2 p, FP y, FVector2 closest, int triangle, ref UInt128 bestDistanceSqr, ref FVector2 bestPoint, ref int bestTriangle) {
		var dx = AbsDifference(p.X.RawValue, closest.X.RawValue);
		var dy = AbsDifference(y.RawValue, SampleHeight(closest, triangle).RawValue);
		var dz = AbsDifference(p.Y.RawValue, closest.Y.RawValue);
		var distanceSqr = dx * dx + dy * dy + dz * dz;
		if (distanceSqr < bestDistanceSqr) {
			bestDistanceSqr = distanceSqr;
			bestPoint = closest;
			bestTriangle = triangle;
		}
	}

	private static UInt128 AbsDifference(long a, long b) {
		var difference = (Int128)a - b;
		return (UInt128)(difference < 0 ? -difference : difference);
	}
}
