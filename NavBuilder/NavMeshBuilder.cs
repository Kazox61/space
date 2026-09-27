// Derived from xpTURN Klotho 0.14.1 (FPNavMeshBuildPipeline.cs, full-build path), Apache-2.0.
// Modified for Space: Fixed64 math, exact integer T-junction predicate on the snap grid,
// canonical vertex/triangle ordering, unreferenced vertices dropped, non-manifold edges left open,
// failures thrown instead of logged, rebake pooling and incremental paths not ported.
using Fixed64;
using Space.GameCore;

namespace Space.NavBuilder;

/// <summary>What the build changed on the way from input triangles to the mesh.</summary>
public readonly record struct NavMeshBuildReport(
	int InputTriangles,
	int OutputTriangles,
	int SnappedVertices,
	int WeldedVertices,
	int OrientationFlips,
	int DegenerateTriangles,
	int DuplicateTriangles,
	int TJunctionSplits,
	int NonManifoldEdges
);

public readonly record struct NavMeshBuildResult(NavMesh Mesh, NavMeshBuildReport Report) {
	/// <summary>Zone index of each mesh triangle, or -1.</summary>
	public int[] TriangleZones { get; init; } = [];
}

/// <summary>
/// Offline pipeline from a fixed-point triangle soup to a runtime <see cref="NavMesh"/>: snap to
/// <see cref="NavSnapGrid"/>, weld, remove degenerate triangles, split T-junctions, canonicalize,
/// then build adjacency, portals, bounds, and the lookup grid.
/// </summary>
/// <remarks>
/// The output depends only on the canonical form of the input: vertex numbering, unreferenced
/// vertices, triangle order, triangle rotation, and winding do not change a single byte.
/// </remarks>
public static class NavMeshBuilder {
	/// <summary>Largest accepted |X| and |Z|; see <see cref="NavMesh.MaxCoordinate"/>.</summary>
	public static FP MaxCoordinate => NavMesh.MaxCoordinate;

	/// <summary>Highest area index; a triangle's mask is <c>1 &lt;&lt; area</c>.</summary>
	public const int MaxArea = 31;

	/// <summary>Triangles with |XZ area| below this are dropped.</summary>
	public static readonly FP DegenerateAreaEpsilon = FP.FromRatio(1, 10000);

	/// <summary>Perpendicular distance, in snap-grid steps, within which a vertex counts as on an edge.</summary>
	public const long TJunctionEpsilon = 2;

	/// <summary>Vertical distance within which a vertex on an edge counts as on it (separates floors).</summary>
	public static readonly FP TJunctionHeightEpsilon = FP.Half;

	private const int AreaBits = 5;
	private const int MaxTJunctionIterations = 10;
	private const int MaxTJunctionGrowth = 20;

	/// <param name="vertices">World-space vertices; Y is up.</param>
	/// <param name="indices">Triangle vertex indices, three per triangle.</param>
	/// <param name="areas">Area index per triangle, in [0, <see cref="MaxArea"/>].</param>
	/// <param name="cellSize">Lookup grid cell size in world units.</param>
	public static NavMeshBuildResult Build(ReadOnlySpan<FVector3> vertices, ReadOnlySpan<int> indices, ReadOnlySpan<int> areas, FP cellSize) {
		return Build(vertices, indices, areas, default, cellSize);
	}

	/// <param name="zones">
	/// Zone index per triangle, in [-1, <see cref="NavZoneData.MaxZones"/>), -1 for none; or empty
	/// when no triangle belongs to a zone. Split triangles keep their zone, and the result reports
	/// each output triangle's zone in <see cref="NavMeshBuildResult.TriangleZones"/>.
	/// </param>
	public static NavMeshBuildResult Build(ReadOnlySpan<FVector3> vertices, ReadOnlySpan<int> indices, ReadOnlySpan<int> areas, ReadOnlySpan<int> zones, FP cellSize) {
		ValidateInput(vertices, indices, areas, zones, cellSize);
		var inputTriangles = indices.Length / 3;

		var tags = new int[areas.Length];
		for (var t = 0; t < tags.Length; t++) {
			tags[t] = Tag(areas[t], zones.IsEmpty ? -1 : zones[t]);
		}

		var snapped = SnapAndWeld(vertices, indices, out var weldedIndices, out var movedCount, out var weldCount, out var flipCount);
		var soup = Canonicalize(snapped, weldedIndices, tags);
		var splits = SplitTJunctions(ref soup);
		var final = Canonicalize(soup.Vertices, soup.Indices, soup.Tags);

		if (final.Tags.Length == 0) {
			throw new InvalidDataException("Navmesh build removed every triangle.");
		}

		var mesh = Assemble(final, cellSize, out var nonManifoldEdges);
		var triangleZones = new int[final.Tags.Length];
		for (var t = 0; t < triangleZones.Length; t++) {
			triangleZones[t] = TagZone(final.Tags[t]);
		}
		var report = new NavMeshBuildReport(
			InputTriangles: inputTriangles,
			OutputTriangles: mesh.TriangleCount,
			SnappedVertices: movedCount,
			WeldedVertices: weldCount,
			OrientationFlips: flipCount,
			DegenerateTriangles: soup.DegenerateRemoved + final.DegenerateRemoved,
			DuplicateTriangles: soup.DuplicatesRemoved + final.DuplicatesRemoved,
			TJunctionSplits: splits,
			NonManifoldEdges: nonManifoldEdges
		);
		return new NavMeshBuildResult(mesh, report) { TriangleZones = triangleZones };
	}

	/// <summary>
	/// The per-triangle value the pipeline carries: area in the low bits, zone + 1 above them, so
	/// sorting and duplicate removal treat it as one integer.
	/// </summary>
	private static int Tag(int area, int zone) => area | ((zone + 1) << AreaBits);

	private static int TagArea(int tag) => tag & ((1 << AreaBits) - 1);

	private static int TagZone(int tag) => (tag >> AreaBits) - 1;

	private static void ValidateInput(ReadOnlySpan<FVector3> vertices, ReadOnlySpan<int> indices, ReadOnlySpan<int> areas, ReadOnlySpan<int> zones, FP cellSize) {
		if (indices.Length % 3 != 0) {
			throw new ArgumentException($"Index count {indices.Length} is not a multiple of three.", nameof(indices));
		}
		if (areas.Length != indices.Length / 3) {
			throw new ArgumentException($"Area count {areas.Length} does not match triangle count {indices.Length / 3}.", nameof(areas));
		}
		if (!zones.IsEmpty && zones.Length != areas.Length) {
			throw new ArgumentException($"Zone count {zones.Length} does not match triangle count {areas.Length}.", nameof(zones));
		}
		if (cellSize <= FP.Zero) {
			throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
		}
		foreach (var index in indices) {
			if ((uint)index >= (uint)vertices.Length) {
				throw new ArgumentException($"Index {index} is outside the {vertices.Length} vertices.", nameof(indices));
			}
		}
		for (var t = 0; t < areas.Length; t++) {
			if ((uint)areas[t] > MaxArea) {
				throw new ArgumentException($"Triangle {t} area {areas[t]} is outside [0, {MaxArea}].", nameof(areas));
			}
		}
		foreach (var zone in zones) {
			if (zone < -1 || zone >= NavZoneData.MaxZones) {
				throw new ArgumentException($"Zone {zone} is outside [-1, {NavZoneData.MaxZones}).", nameof(zones));
			}
		}
		for (var i = 0; i < vertices.Length; i++) {
			var v = vertices[i];
			if (FP.Abs(v.X) > MaxCoordinate || FP.Abs(v.Z) > MaxCoordinate) {
				throw new ArgumentException($"Vertex {i} ({v}) exceeds the ±{MaxCoordinate} XZ domain.", nameof(vertices));
			}
		}
	}

	/// <summary>
	/// Snaps X/Z onto the predicate grid (Y is untouched: it separates floors) and welds vertices
	/// that became exactly coincident, first occurrence winning.
	/// </summary>
	private static FVector3[] SnapAndWeld(ReadOnlySpan<FVector3> vertices, ReadOnlySpan<int> indices, out int[] weldedIndices, out int movedCount, out int weldCount, out int flipCount) {
		var snapped = new FVector3[vertices.Length];
		movedCount = 0;
		for (var i = 0; i < vertices.Length; i++) {
			var v = vertices[i];
			snapped[i] = new FVector3(NavSnapGrid.Quantize(v.X), v.Y, NavSnapGrid.Quantize(v.Z));
			if (!NavSnapGrid.IsOnGrid(v.X) || !NavSnapGrid.IsOnGrid(v.Z)) {
				movedCount++;
			}
		}

		flipCount = 0;
		for (var i = 0; i < indices.Length; i += 3) {
			var before = SignedArea(vertices, indices[i], indices[i + 1], indices[i + 2]);
			var after = SignedArea(snapped, indices[i], indices[i + 1], indices[i + 2]);
			if ((before > FP.Zero && after < FP.Zero) || (before < FP.Zero && after > FP.Zero)) {
				flipCount++;
			}
		}

		var canonical = new Dictionary<(long, long, long), int>(snapped.Length);
		var remap = new int[snapped.Length];
		weldCount = 0;
		for (var i = 0; i < snapped.Length; i++) {
			var key = (snapped[i].X.RawValue, snapped[i].Y.RawValue, snapped[i].Z.RawValue);
			if (canonical.TryGetValue(key, out var first)) {
				remap[i] = first;
				weldCount++;
			} else {
				canonical.Add(key, i);
				remap[i] = i;
			}
		}

		weldedIndices = new int[indices.Length];
		for (var i = 0; i < indices.Length; i++) {
			weldedIndices[i] = remap[indices[i]];
		}
		return snapped;
	}

	private struct Soup {
		public FVector3[] Vertices;
		public int[] Indices;
		public int[] Tags;
		public int DegenerateRemoved;
		public int DuplicatesRemoved;
	}

	/// <summary>
	/// Drops degenerate triangles and unreferenced vertices, sorts vertices by (X, Z, Y), winds every
	/// triangle counter-clockwise in XZ starting at its lowest vertex, sorts triangles by
	/// (vertices, tag), and drops repeated triangles, keeping the lowest tag (area first, then zone).
	/// </summary>
	private static Soup Canonicalize(FVector3[] vertices, int[] indices, int[] tags) {
		var kept = new List<int>(tags.Length);
		for (var t = 0; t < tags.Length; t++) {
			if (!IsDegenerate(vertices, indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2])) {
				kept.Add(t);
			}
		}

		var referenced = new List<int>();
		var seen = new bool[vertices.Length];
		foreach (var t in kept) {
			for (var c = 0; c < 3; c++) {
				var v = indices[t * 3 + c];
				if (!seen[v]) {
					seen[v] = true;
					referenced.Add(v);
				}
			}
		}
		referenced.Sort((a, b) => CompareVertices(vertices[a], vertices[b]));
		var remap = new int[vertices.Length];
		var outVertices = new FVector3[referenced.Count];
		for (var i = 0; i < referenced.Count; i++) {
			remap[referenced[i]] = i;
			outVertices[i] = vertices[referenced[i]];
		}

		var triangles = new List<(int V0, int V1, int V2, int Tag)>(kept.Count);
		foreach (var t in kept) {
			var a = remap[indices[t * 3]];
			var b = remap[indices[t * 3 + 1]];
			var c = remap[indices[t * 3 + 2]];
			if (SignedArea(outVertices, a, b, c) < FP.Zero) {
				(b, c) = (c, b);
			}
			while (a > b || a > c) {
				(a, b, c) = (b, c, a);
			}
			triangles.Add((a, b, c, tags[t]));
		}
		triangles.Sort();

		var outIndices = new List<int>(triangles.Count * 3);
		var outTags = new List<int>(triangles.Count);
		var duplicates = 0;
		for (var i = 0; i < triangles.Count; i++) {
			var tri = triangles[i];
			if (i > 0 && (tri.V0, tri.V1, tri.V2) == (triangles[i - 1].V0, triangles[i - 1].V1, triangles[i - 1].V2)) {
				duplicates++;
				continue;
			}
			outIndices.Add(tri.V0);
			outIndices.Add(tri.V1);
			outIndices.Add(tri.V2);
			outTags.Add(tri.Tag);
		}

		return new Soup {
			Vertices = outVertices,
			Indices = outIndices.ToArray(),
			Tags = outTags.ToArray(),
			DegenerateRemoved = tags.Length - kept.Count,
			DuplicatesRemoved = duplicates,
		};
	}

	private static int CompareVertices(FVector3 a, FVector3 b) {
		var c = a.X.RawValue.CompareTo(b.X.RawValue);
		if (c != 0) {
			return c;
		}
		c = a.Z.RawValue.CompareTo(b.Z.RawValue);
		return c != 0 ? c : a.Y.RawValue.CompareTo(b.Y.RawValue);
	}

	private static bool IsDegenerate(FVector3[] vertices, int v0, int v1, int v2) {
		return v0 == v1 || v1 == v2 || v2 == v0 || FP.Abs(SignedArea(vertices, v0, v1, v2)) < DegenerateAreaEpsilon;
	}

	private static FP SignedArea(ReadOnlySpan<FVector3> vertices, int v0, int v1, int v2) {
		return NavGeometry.SignedArea(NavGeometry.ToXZ(vertices[v0]), NavGeometry.ToXZ(vertices[v1]), NavGeometry.ToXZ(vertices[v2]));
	}

	/// <summary>
	/// Where a vertex lies on another triangle's edge, fans that triangle out through the vertex so
	/// every shared edge is shared exactly. Splits reuse existing vertices, so the output stays on
	/// the grid. Repeats until no triangle splits.
	/// </summary>
	private static int SplitTJunctions(ref Soup soup) {
		var vertices = soup.Vertices;
		var sx = new long[vertices.Length];
		var sz = new long[vertices.Length];
		for (var i = 0; i < vertices.Length; i++) {
			sx[i] = NavSnapGrid.Snap(vertices[i].X);
			sz[i] = NavSnapGrid.Snap(vertices[i].Z);
		}

		var indices = soup.Indices;
		var tags = soup.Tags;
		var initialCount = tags.Length;
		var totalSplits = 0;
		var mids = new List<(Int128 Dot, int Vertex)>();

		for (var iteration = 0; ; iteration++) {
			if (iteration == MaxTJunctionIterations) {
				throw new InvalidDataException($"T-junction splitting did not converge after {MaxTJunctionIterations} iterations.");
			}
			if (tags.Length > initialCount * MaxTJunctionGrowth) {
				throw new InvalidDataException($"T-junction splitting grew {initialCount} triangles to {tags.Length}; the input geometry is likely broken.");
			}

			var newIndices = new List<int>(indices.Length);
			var newTags = new List<int>(tags.Length);
			var splits = 0;
			for (var t = 0; t < tags.Length; t++) {
				var i0 = indices[t * 3];
				var i1 = indices[t * 3 + 1];
				var i2 = indices[t * 3 + 2];

				var splitEdge = -1;
				for (var e = 0; e < 3 && splitEdge < 0; e++) {
					var (ea, eb) = e switch { 0 => (i0, i1), 1 => (i1, i2), _ => (i2, i0) };
					mids.Clear();
					FindVerticesOnEdge(vertices, sx, sz, ea, eb, mids);
					if (mids.Count > 0) {
						splitEdge = e;
					}
				}

				if (splitEdge < 0) {
					newIndices.Add(i0);
					newIndices.Add(i1);
					newIndices.Add(i2);
					newTags.Add(tags[t]);
					continue;
				}

				mids.Sort();
				var (eA, eB, eC) = splitEdge switch { 0 => (i0, i1, i2), 1 => (i1, i2, i0), _ => (i2, i0, i1) };
				var previous = eA;
				foreach (var mid in mids) {
					newIndices.Add(previous);
					newIndices.Add(mid.Vertex);
					newIndices.Add(eC);
					newTags.Add(tags[t]);
					previous = mid.Vertex;
				}
				newIndices.Add(previous);
				newIndices.Add(eB);
				newIndices.Add(eC);
				newTags.Add(tags[t]);
				splits++;
			}

			indices = newIndices.ToArray();
			tags = newTags.ToArray();
			totalSplits += splits;
			if (splits == 0) {
				break;
			}
		}

		soup.Indices = indices;
		soup.Tags = tags;
		return totalSplits;
	}

	/// <summary>
	/// Vertices strictly inside edge (a, b) in XZ, within <see cref="TJunctionEpsilon"/> of it and
	/// <see cref="TJunctionHeightEpsilon"/> of its height, keyed by distance along the edge. Exact
	/// integer arithmetic on the snap grid. Vertices are sorted by X, so candidates are a range.
	/// </summary>
	private static void FindVerticesOnEdge(FVector3[] vertices, long[] sx, long[] sz, int a, int b, List<(Int128 Dot, int Vertex)> result) {
		long ax = sx[a], az = sz[a], bx = sx[b], bz = sz[b];
		long abx = bx - ax, abz = bz - az;
		Int128 lengthSqr = (Int128)abx * abx + (Int128)abz * abz;
		Int128 epsilonSqr = TJunctionEpsilon * TJunctionEpsilon;
		if (lengthSqr < epsilonSqr) {
			return;
		}

		var first = LowerBound(sx, Math.Min(ax, bx) - TJunctionEpsilon);
		var last = Math.Max(ax, bx) + TJunctionEpsilon;
		var aY = vertices[a].Y;
		var abY = vertices[b].Y - aY;
		for (var v = first; v < sx.Length && sx[v] <= last; v++) {
			if (v == a || v == b) {
				continue;
			}
			long apx = sx[v] - ax, apz = sz[v] - az;
			Int128 cross = (Int128)apx * abz - (Int128)apz * abx;
			if (cross * cross > epsilonSqr * lengthSqr) {
				continue;
			}
			long bpx = sx[v] - bx, bpz = sz[v] - bz;
			if ((Int128)apx * apx + (Int128)apz * apz < epsilonSqr || (Int128)bpx * bpx + (Int128)bpz * bpz < epsilonSqr) {
				continue;
			}
			Int128 dot = (Int128)apx * abx + (Int128)apz * abz;
			if (dot <= 0 || dot >= lengthSqr) {
				continue;
			}
			var t = FP.FromRaw((long)((dot << FP.FractionalBits) / lengthSqr));
			if (FP.Abs(vertices[v].Y - (aY + abY * t)) > TJunctionHeightEpsilon) {
				continue;
			}
			result.Add((dot, v));
		}
	}

	private static int LowerBound(long[] sorted, long value) {
		int lo = 0, hi = sorted.Length;
		while (lo < hi) {
			var mid = (lo + hi) >>> 1;
			if (sorted[mid] < value) {
				lo = mid + 1;
			} else {
				hi = mid;
			}
		}
		return lo;
	}

	private static NavMesh Assemble(in Soup soup, FP cellSize, out int nonManifoldEdges) {
		var vertices = soup.Vertices;
		var indices = soup.Indices;
		var count = soup.Tags.Length;
		var neighbors = BuildAdjacency(indices, count, out nonManifoldEdges);

		var triangles = new NavTriangle[count];
		var areas = new NavTriangleArea[count];
		for (var t = 0; t < count; t++) {
			triangles[t] = NavTriangle.Create(vertices, indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], neighbors[t * 3], neighbors[t * 3 + 1], neighbors[t * 3 + 2]);
			areas[t] = new NavTriangleArea { AreaMask = 1 << TagArea(soup.Tags[t]), CostMultiplier = FP.One };
		}

		var min = NavGeometry.ToXZ(vertices[0]);
		var max = min;
		foreach (var v in vertices) {
			var xz = NavGeometry.ToXZ(v);
			min = FVector2.MinComponents(min, xz);
			max = FVector2.MaxComponents(max, xz);
		}
		var bounds = new FAABB2(min, max);
		BuildGrid(vertices, indices, count, bounds, cellSize, out var gridCells, out var gridTriangles, out var width, out var height);

		return new NavMesh(vertices, triangles, areas, bounds, gridCells, gridTriangles, width, height, cellSize, bounds.Min);
	}

	/// <summary>
	/// Pairs triangles over shared edges. An edge used by more than two triangles is ambiguous and
	/// left as a boundary on every triangle that uses it.
	/// </summary>
	private static int[] BuildAdjacency(int[] indices, int count, out int nonManifoldEdges) {
		var records = new (long Key, int Slot)[count * 3];
		for (var slot = 0; slot < records.Length; slot++) {
			var t = slot / 3;
			var va = indices[slot];
			var vb = indices[t * 3 + (slot % 3 + 1) % 3];
			records[slot] = (((long)Math.Min(va, vb) << 32) | (uint)Math.Max(va, vb), slot);
		}
		Array.Sort(records);

		var neighbors = new int[count * 3];
		Array.Fill(neighbors, -1);
		nonManifoldEdges = 0;
		for (var i = 0; i < records.Length;) {
			var end = i + 1;
			while (end < records.Length && records[end].Key == records[i].Key) {
				end++;
			}
			if (end - i == 2) {
				neighbors[records[i].Slot] = records[i + 1].Slot / 3;
				neighbors[records[i + 1].Slot] = records[i].Slot / 3;
			} else if (end - i > 2) {
				nonManifoldEdges++;
			}
			i = end;
		}
		return neighbors;
	}

	/// <summary>Buckets each triangle into every cell its XZ bounding box touches, in triangle order.</summary>
	private static void BuildGrid(FVector3[] vertices, int[] indices, int count, FAABB2 bounds, FP cellSize, out int[] gridCells, out int[] gridTriangles, out int width, out int height) {
		width = CellsToCover(bounds.Size.X, cellSize);
		height = CellsToCover(bounds.Size.Y, cellSize);
		if ((long)width * height > NavMesh.MaxGridCells) {
			throw new InvalidDataException($"Lookup grid {width}x{height} exceeds {NavMesh.MaxGridCells} cells; use a larger cell size.");
		}

		var cells = new List<int>[width * height];
		for (var i = 0; i < cells.Length; i++) {
			cells[i] = [];
		}
		for (var t = 0; t < count; t++) {
			var a = NavGeometry.ToXZ(vertices[indices[t * 3]]);
			var b = NavGeometry.ToXZ(vertices[indices[t * 3 + 1]]);
			var c = NavGeometry.ToXZ(vertices[indices[t * 3 + 2]]);
			var triMin = FVector2.MinComponents(FVector2.MinComponents(a, b), c) - bounds.Min;
			var triMax = FVector2.MaxComponents(FVector2.MaxComponents(a, b), c) - bounds.Min;
			var colMin = Math.Max(0, (triMin.X / cellSize).ToInt());
			var colMax = Math.Min(width - 1, (triMax.X / cellSize).ToInt());
			var rowMin = Math.Max(0, (triMin.Y / cellSize).ToInt());
			var rowMax = Math.Min(height - 1, (triMax.Y / cellSize).ToInt());
			for (var row = rowMin; row <= rowMax; row++) {
				for (var col = colMin; col <= colMax; col++) {
					cells[row * width + col].Add(t);
				}
			}
		}

		gridCells = new int[cells.Length * 2];
		gridTriangles = new int[cells.Sum(static cell => cell.Count)];
		var offset = 0;
		for (var i = 0; i < cells.Length; i++) {
			gridCells[i * 2] = offset;
			gridCells[i * 2 + 1] = cells[i].Count;
			cells[i].CopyTo(gridTriangles, offset);
			offset += cells[i].Count;
		}
	}

	/// <summary>
	/// Cells needed to cover <paramref name="size"/>. The division truncates, so the estimate is
	/// corrected until the covered length really reaches the size.
	/// </summary>
	private static int CellsToCover(FP size, FP cellSize) {
		var cells = Math.Max(1, FP.CeilToInt(size / cellSize));
		while (cellSize * cells < size) {
			cells++;
		}
		return cells;
	}
}
