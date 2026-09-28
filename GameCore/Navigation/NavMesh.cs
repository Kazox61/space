// Derived from xpTURN Klotho 0.14.1 (FPNavMesh.cs), Apache-2.0.
// Modified for Space: Fixed64 math, exact-size storage without rebake pooling, runtime attributes
// split from geometry, constructor validation, grid lookup clamped to the covered bounds.
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Deterministic fixed-point navmesh. Vertices (Y up, XZ planar), triangles, and the spatial
/// lookup grid are immutable. Only <see cref="Areas"/> may change at runtime; see
/// <see cref="NavTriangleArea"/>.
/// </summary>
public sealed class NavMesh {
	/// <summary>Largest lookup grid, in cells, a mesh may carry.</summary>
	public const int MaxGridCells = 1 << 22;

	/// <summary>
	/// Largest |coordinate| a baked or decoded navmesh vertex may have. Guarantees coordinate-derived
	/// runtime products, including the squared-length products in <see cref="NavGeometry.Barycentric"/>,
	/// remain within the <see cref="FP"/> range.
	/// </summary>
	public static readonly FP MaxCoordinate = (1 << 6).ToFP();

	private readonly FVector3[] _vertices;
	private readonly NavTriangle[] _triangles;
	private readonly NavTriangleArea[] _areas;
	private readonly int[] _gridCells;
	private readonly int[] _gridTriangles;

	public readonly FAABB2 BoundsXZ;
	public readonly int GridWidth;
	public readonly int GridHeight;
	public readonly FP GridCellSize;
	public readonly FVector2 GridOrigin;

	/// <param name="gridCells">Per cell (row-major): start index into <paramref name="gridTriangles"/>, then count.</param>
	/// <param name="gridTriangles">Triangle indices referenced by <paramref name="gridCells"/>.</param>
	public NavMesh(
		FVector3[] vertices,
		NavTriangle[] triangles,
		NavTriangleArea[] areas,
		FAABB2 boundsXZ,
		int[] gridCells,
		int[] gridTriangles,
		int gridWidth,
		int gridHeight,
		FP gridCellSize,
		FVector2 gridOrigin
	) {
		ArgumentNullException.ThrowIfNull(vertices);
		ArgumentNullException.ThrowIfNull(triangles);
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(gridCells);
		ArgumentNullException.ThrowIfNull(gridTriangles);

		_vertices = vertices;
		_triangles = triangles;
		_areas = areas;
		_gridCells = gridCells;
		_gridTriangles = gridTriangles;
		BoundsXZ = boundsXZ;
		GridWidth = gridWidth;
		GridHeight = gridHeight;
		GridCellSize = gridCellSize;
		GridOrigin = gridOrigin;

		Validate();
	}

	private NavMesh(NavMesh source, NavTriangleArea[] areas) {
		_vertices = source._vertices;
		_triangles = source._triangles;
		_areas = areas;
		_gridCells = source._gridCells;
		_gridTriangles = source._gridTriangles;
		BoundsXZ = source.BoundsXZ;
		GridWidth = source.GridWidth;
		GridHeight = source.GridHeight;
		GridCellSize = source.GridCellSize;
		GridOrigin = source.GridOrigin;
	}

	/// <summary>
	/// A mesh sharing this one's immutable geometry and grid, with its own copy of
	/// <see cref="Areas"/>, so runtime attribute writes never reach the original.
	/// </summary>
	public NavMesh CopyWithOwnAreas() {
		return new NavMesh(this, (NavTriangleArea[])_areas.Clone());
	}

	public ReadOnlySpan<FVector3> Vertices => _vertices;
	public ReadOnlySpan<NavTriangle> Triangles => _triangles;
	public ReadOnlySpan<int> GridCells => _gridCells;
	public ReadOnlySpan<int> GridTriangles => _gridTriangles;

	/// <summary>Mutable per-triangle attributes, indexed like <see cref="Triangles"/>.</summary>
	public Span<NavTriangleArea> Areas => _areas;

	public int TriangleCount => _triangles.Length;

	/// <summary>Grid cell coordinates for a point. The result may lie outside the grid.</summary>
	public void GetCellCoords(FVector2 xz, out int col, out int row) {
		var local = xz - GridOrigin;
		col = (local.X / GridCellSize).ToInt();
		row = (local.Y / GridCellSize).ToInt();
	}

	/// <summary>
	/// Grid cell for a point inside <see cref="BoundsXZ"/>. Points on the far bounds edge are
	/// clamped into the last cell, which the constructor guarantees covers them.
	/// </summary>
	public bool TryGetContainingCell(FVector2 xz, out int col, out int row) {
		if (!BoundsXZ.Contains(xz)) {
			col = row = -1;
			return false;
		}
		GetCellCoords(xz, out col, out row);
		col = Math.Clamp(col, 0, GridWidth - 1);
		row = Math.Clamp(row, 0, GridHeight - 1);
		return true;
	}

	public bool IsCellValid(int col, int row) {
		return col >= 0 && col < GridWidth && row >= 0 && row < GridHeight;
	}

	/// <summary>The cell's triangles as a slice of <see cref="GridTriangles"/>.</summary>
	public ReadOnlySpan<int> GetCellTriangles(int col, int row) {
		var cell = (row * GridWidth + col) * 2;
		return new ReadOnlySpan<int>(_gridTriangles, _gridCells[cell], _gridCells[cell + 1]);
	}

	public FVector2 GetVertexXZ(int vertex) {
		return NavGeometry.ToXZ(_vertices[vertex]);
	}

	private void Validate() {
		foreach (var vertex in _vertices) {
			if (FP.Abs(vertex.X) > MaxCoordinate || FP.Abs(vertex.Y) > MaxCoordinate || FP.Abs(vertex.Z) > MaxCoordinate) {
				throw new ArgumentException($"Vertex ({vertex}) exceeds the +/-{MaxCoordinate} domain.");
			}
		}
		if (_areas.Length != _triangles.Length) {
			throw new ArgumentException($"Area count {_areas.Length} does not match triangle count {_triangles.Length}.");
		}
		for (var t = 0; t < _triangles.Length; t++) {
			ValidateTriangle(t);
		}

		if (GridWidth <= 0 || GridHeight <= 0 || (long)GridWidth * GridHeight > MaxGridCells) {
			throw new ArgumentException($"Grid size {GridWidth}x{GridHeight} must be positive and at most {MaxGridCells} cells.");
		}
		if (GridCellSize <= FP.Zero) {
			throw new ArgumentException("Grid cell size must be positive.");
		}
		var gridMax = GridOrigin + new FVector2(GridCellSize * GridWidth, GridCellSize * GridHeight);
		if (BoundsXZ.Min.X < GridOrigin.X || BoundsXZ.Min.Y < GridOrigin.Y || BoundsXZ.Max.X > gridMax.X || BoundsXZ.Max.Y > gridMax.Y) {
			throw new ArgumentException("The lookup grid must cover the XZ bounds.");
		}
		if ((long)GridWidth * GridHeight * 2 != _gridCells.Length) {
			throw new ArgumentException($"Grid cell array length {_gridCells.Length} does not match {GridWidth}x{GridHeight} cells.");
		}
		for (var cell = 0; cell < _gridCells.Length; cell += 2) {
			var start = _gridCells[cell];
			var count = _gridCells[cell + 1];
			if (start < 0 || count < 0 || start > _gridTriangles.Length - count) {
				throw new ArgumentException($"Grid cell {cell / 2} range [{start}, +{count}) is out of range.");
			}
		}
		foreach (var triangle in _gridTriangles) {
			if ((uint)triangle >= (uint)_triangles.Length) {
				throw new ArgumentException($"Grid references triangle {triangle}, which does not exist.");
			}
		}
	}

	private void ValidateTriangle(int t) {
		var triangle = _triangles[t];
		for (var corner = 0; corner < 3; corner++) {
			if ((uint)triangle.GetVertex(corner) >= (uint)_vertices.Length) {
				throw new ArgumentException($"Triangle {t} references vertex {triangle.GetVertex(corner)}, which does not exist.");
			}
		}
		if (triangle.V0 == triangle.V1 || triangle.V1 == triangle.V2 || triangle.V2 == triangle.V0) {
			throw new ArgumentException($"Triangle {t} repeats a vertex.");
		}
		if (FP.Abs(NavGeometry.SignedArea(GetVertexXZ(triangle.V0), GetVertexXZ(triangle.V1), GetVertexXZ(triangle.V2))) < NavGeometry.MinTriangleArea) {
			throw new ArgumentException($"Triangle {t} is too thin in XZ.");
		}

		for (var e = 0; e < 3; e++) {
			var neighbor = triangle.GetNeighbor(e);
			if (neighbor < 0) {
				continue;
			}
			if (neighbor >= _triangles.Length || neighbor == t) {
				throw new ArgumentException($"Triangle {t} edge {e} has invalid neighbor {neighbor}.");
			}
			triangle.GetEdgeVertices(e, out var va, out var vb);
			if (!PointsBack(_triangles[neighbor], t, va, vb)) {
				throw new ArgumentException($"Triangle {t} edge {e} names neighbor {neighbor}, which does not share that edge back.");
			}
		}
	}

	private static bool PointsBack(in NavTriangle neighbor, int triangle, int va, int vb) {
		for (var e = 0; e < 3; e++) {
			neighbor.GetEdgeVertices(e, out var na, out var nb);
			if (neighbor.GetNeighbor(e) == triangle && ((na == va && nb == vb) || (na == vb && nb == va))) {
				return true;
			}
		}
		return false;
	}
}
