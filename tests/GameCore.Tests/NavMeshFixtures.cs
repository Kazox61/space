using Fixed64;

namespace Space.GameCore.Tests;

/// <summary>
/// Handcrafted navmeshes for runtime tests. Adjacency and the lookup grid are derived by brute
/// force here so fixtures only list vertices and triangles.
/// </summary>
internal static class NavMeshFixtures {
	/// <summary>
	/// Two 4x4 squares along X, each split along its rising diagonal.
	/// <code>
	///   v2(0,4)---v3(4,4)---v5(8,4)
	///     | T1  /   | T3  /   |
	///     |   /  T0 |   /  T2 |
	///   v0(0,0)---v1(4,0)---v4(8,0)
	/// </code>
	/// </summary>
	public static NavMesh Strip() {
		return Build(
			[V(0, 0, 0), V(4, 0, 0), V(0, 0, 4), V(4, 0, 4), V(8, 0, 0), V(8, 0, 4)],
			[0, 1, 3, 0, 3, 2, 1, 4, 5, 1, 5, 3],
			cellSize: 4
		);
	}

	/// <summary>
	/// A 6x6 square with a 2x2 hole at [2,4]x[2,4], built from 2x2 cells split along their falling
	/// diagonals. The layout, including the corner triangles at (0,0) and (6,6), is mirror-symmetric
	/// about x = z, so routes between those corners around either side of the hole cost exactly the
	/// same. Cell (col, row) holds triangles 2k (lower-left) and 2k + 1 (upper-right), where k counts
	/// cells row-major skipping the hole.
	/// </summary>
	public static NavMesh Ring(bool clockwise = false) {
		var vertices = new List<FVector3>();
		for (var z = 0; z <= 6; z += 2) {
			for (var x = 0; x <= 6; x += 2) {
				vertices.Add(V(x, 0, z));
			}
		}
		var indices = new List<int>();
		for (var row = 0; row < 3; row++) {
			for (var col = 0; col < 3; col++) {
				if (row == 1 && col == 1) {
					continue;
				}
				var v00 = row * 4 + col;
				var v10 = v00 + 1;
				var v01 = v00 + 4;
				var v11 = v01 + 1;
				AddTriangle(indices, clockwise, v00, v10, v01);
				AddTriangle(indices, clockwise, v10, v11, v01);
			}
		}
		return Build(vertices.ToArray(), indices.ToArray(), cellSize: 2);
	}

	/// <summary>
	/// Two disconnected 4x4 floors stacked at y = 0 (T0, T1) and y = 3 (T2, T3).
	/// </summary>
	public static NavMesh TwoFloors() {
		return Build(
			[
				V(0, 0, 0), V(4, 0, 0), V(0, 0, 4), V(4, 0, 4),
				V(0, 3, 0), V(4, 3, 0), V(0, 3, 4), V(4, 3, 4),
			],
			[0, 1, 3, 0, 3, 2, 4, 5, 7, 4, 7, 6],
			cellSize: 4
		);
	}

	/// <summary>A 4x4 square rising from y = 0 at x = 0 to y = 2 at x = 4.</summary>
	public static NavMesh Slope() {
		return Build(
			[V(0, 0, 0), V(4, 2, 0), V(0, 0, 4), V(4, 2, 4)],
			[0, 1, 3, 0, 3, 2],
			cellSize: 4
		);
	}

	public static FVector3 V(int x, int y, int z) {
		return new FVector3(x.ToFP(), y.ToFP(), z.ToFP());
	}

	public static FVector2 XZ(int x, int z) {
		return new FVector2(x.ToFP(), z.ToFP());
	}

	private static void AddTriangle(List<int> indices, bool clockwise, int a, int b, int c) {
		indices.Add(a);
		indices.Add(clockwise ? c : b);
		indices.Add(clockwise ? b : c);
	}

	private static NavMesh Build(FVector3[] vertices, int[] indices, int cellSize) {
		var triangleCount = indices.Length / 3;
		var triangles = new NavTriangle[triangleCount];
		for (var t = 0; t < triangleCount; t++) {
			triangles[t] = NavTriangle.Create(
				vertices,
				indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2],
				FindNeighbor(indices, t, 0), FindNeighbor(indices, t, 1), FindNeighbor(indices, t, 2)
			);
		}
		var areas = new NavTriangleArea[triangleCount];
		Array.Fill(areas, NavTriangleArea.Default);

		var min = new FVector2(vertices.Min(static v => v.X), vertices.Min(static v => v.Z));
		var max = new FVector2(vertices.Max(static v => v.X), vertices.Max(static v => v.Z));
		var cell = cellSize.ToFP();
		// Exact cover, so points on the far bounds edge exercise the mesh's cell clamp.
		var width = Math.Max(1, FP.CeilToInt((max.X - min.X) / cell));
		var height = Math.Max(1, FP.CeilToInt((max.Y - min.Y) / cell));

		var gridCells = new int[width * height * 2];
		var gridTriangles = new List<int>();
		for (var row = 0; row < height; row++) {
			for (var col = 0; col < width; col++) {
				var cellMin = min + new FVector2(cell * col, cell * row);
				var cellMax = cellMin + new FVector2(cell, cell);
				var cellIndex = row * width + col;
				gridCells[cellIndex * 2] = gridTriangles.Count;
				for (var t = 0; t < triangleCount; t++) {
					if (Overlaps(vertices, indices, t, cellMin, cellMax)) {
						gridTriangles.Add(t);
					}
				}
				gridCells[cellIndex * 2 + 1] = gridTriangles.Count - gridCells[cellIndex * 2];
			}
		}

		return new NavMesh(vertices, triangles, areas, new FAABB2(min, max), gridCells, gridTriangles.ToArray(), width, height, cell, min);
	}

	private static int FindNeighbor(int[] indices, int triangle, int edge) {
		var a = indices[triangle * 3 + edge];
		var b = indices[triangle * 3 + (edge + 1) % 3];
		for (var other = 0; other < indices.Length / 3; other++) {
			if (other == triangle) {
				continue;
			}
			for (var e = 0; e < 3; e++) {
				var oa = indices[other * 3 + e];
				var ob = indices[other * 3 + (e + 1) % 3];
				if ((oa == a && ob == b) || (oa == b && ob == a)) {
					return other;
				}
			}
		}
		return -1;
	}

	private static bool Overlaps(FVector3[] vertices, int[] indices, int triangle, FVector2 cellMin, FVector2 cellMax) {
		var a = vertices[indices[triangle * 3]];
		var b = vertices[indices[triangle * 3 + 1]];
		var c = vertices[indices[triangle * 3 + 2]];
		var minX = FP.Min(a.X, FP.Min(b.X, c.X));
		var maxX = FP.Max(a.X, FP.Max(b.X, c.X));
		var minZ = FP.Min(a.Z, FP.Min(b.Z, c.Z));
		var maxZ = FP.Max(a.Z, FP.Max(b.Z, c.Z));
		return minX <= cellMax.X && maxX >= cellMin.X && minZ <= cellMax.Y && maxZ >= cellMin.Y;
	}
}
