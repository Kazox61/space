using Fixed64;
using NUnit.Framework;
using Space.NavBuilder;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavMeshBuilderTests {
	private static readonly FP s_snapStep = FP.FromRaw(1L << NavSnapGrid.Shift);

	[Test]
	public void OffGridInputSnapsXZAndKeepsYExact() {
		var y = FP.FromRatio(1234567, 10000000);
		FVector3[] vertices = [
			new(R(71, 100000), y, R(33, 100000)),
			new(R(999931, 100000), y, R(89, 100000)),
			new(R(1000077, 100000), y, R(1000013, 100000)),
			new(R(-59, 100000), y, R(999991, 100000)),
		];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 0, 2, 3], [0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(mesh.TriangleCount, Is.EqualTo(2));
			Assert.That(report.SnappedVertices, Is.EqualTo(4));
			foreach (var v in mesh.Vertices.ToArray()) {
				Assert.That(NavSnapGrid.IsOnGrid(v.X) && NavSnapGrid.IsOnGrid(v.Z), Is.True, $"{v} off grid");
				Assert.That(v.Y, Is.EqualTo(y));
			}
		});
	}

	[Test]
	public void RebuildingABuiltMeshIsByteIdentical() {
		var first = NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP()).Mesh;

		var indices = new List<int>();
		foreach (var t in first.Triangles.ToArray()) {
			indices.AddRange([t.V0, t.V1, t.V2]);
		}
		var (second, report) = NavMeshBuilder.Build(first.Vertices, indices.ToArray(), new int[first.TriangleCount], 2.ToFP());

		Assert.That(NavMeshBytes.Of(second), Is.EqualTo(NavMeshBytes.Of(first)));
		Assert.That(report with { InputTriangles = 0, OutputTriangles = 0 }, Is.EqualTo(default(NavMeshBuildReport)));
	}

	[Test]
	public void SnapCoincidenceWeldsAndHealsAdjacency() {
		var sub = R(1, 10000);
		FVector3[] vertices = [
			V(0, 0, 0), V(2, 0, 0), V(2, 0, 2),
			new(sub, FP.Zero, sub), new(2.ToFP() + sub, FP.Zero, 2.ToFP() + sub), V(0, 0, 2),
		];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 3, 4, 5], [0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(report.WeldedVertices, Is.EqualTo(2));
			Assert.That(mesh.Vertices.Length, Is.EqualTo(4));
			Assert.That(NeighborCount(mesh, 0), Is.EqualTo(1));
			Assert.That(NeighborCount(mesh, 1), Is.EqualTo(1));
		});
	}

	[Test]
	public void StackedFloorsAreNeitherWeldedNorConnected() {
		FVector3[] vertices = [V(0, 0, 0), V(4, 0, 0), V(4, 0, 4), V(0, 5, 0), V(4, 5, 0), V(4, 5, 4)];

		var (mesh, _) = NavMeshBuilder.Build(vertices, [0, 1, 2, 3, 4, 5], [0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(mesh.Vertices.Length, Is.EqualTo(6));
			Assert.That(NeighborCount(mesh, 0), Is.Zero);
			Assert.That(NeighborCount(mesh, 1), Is.Zero);
		});
	}

	[Test]
	public void TJunctionIsSplitSoBothSidesConnect() {
		var (mesh, report) = NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP());

		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var status = pathfinder.FindPath(V(1, 0, 3), V(7, 0, 1), 1, new int[16], out _);

		Assert.Multiple(() => {
			Assert.That(report.TJunctionSplits, Is.EqualTo(1));
			Assert.That(mesh.TriangleCount, Is.EqualTo(6));
			Assert.That(report.NonManifoldEdges, Is.Zero);
			Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		});
	}

	[Test]
	public void TJunctionWithinEpsilonSnapsOntoTheSharedVertex() {
		var vertices = TJunctionVertices();
		vertices[4] = new FVector3(4.ToFP() + s_snapStep, FP.Zero, 2.ToFP());

		var (mesh, report) = NavMeshBuilder.Build(vertices, TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP());

		Assert.That(report.TJunctionSplits, Is.EqualTo(1));
		AssertReciprocalPortals(mesh);
	}

	[Test]
	public void TJunctionOnAnotherFloorIsNotSplit() {
		var vertices = TJunctionVertices();
		foreach (var i in new[] { 4, 5, 6 }) {
			vertices[i] = new FVector3(vertices[i].X, 1.ToFP(), vertices[i].Z);
		}

		var (_, report) = NavMeshBuilder.Build(vertices, TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP());

		Assert.That(report.TJunctionSplits, Is.Zero);
	}

	[Test]
	public void DegenerateAndCollapsedTrianglesNeverReachTheMesh() {
		FVector3[] vertices = [
			V(0, 0, 0), V(4, 0, 0), V(4, 0, 4), V(0, 0, 4),
			V(8, 0, 0), V(9, 0, 0), V(10, 0, 0),
			new(R(1, 10000), FP.Zero, FP.Zero), V(2, 0, 3),
		];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 0, 2, 3, 4, 5, 6, 0, 7, 8], [0, 0, 0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(report.DegenerateTriangles, Is.EqualTo(2));
			Assert.That(mesh.TriangleCount, Is.EqualTo(2));
			Assert.That(mesh.Vertices.Length, Is.EqualTo(4), "vertices only degenerate triangles used are dropped");
			foreach (var t in mesh.Triangles.ToArray()) {
				var area = NavGeometry.SignedArea(mesh.GetVertexXZ(t.V0), mesh.GetVertexXZ(t.V1), mesh.GetVertexXZ(t.V2));
				Assert.That(area, Is.GreaterThanOrEqualTo(NavMeshBuilder.DegenerateAreaEpsilon));
			}
		});
	}

	[Test]
	public void SnapOrientationFlipIsReportedAndTheSliverRemoved() {
		var step = 1L << NavSnapGrid.Shift;
		FVector3[] vertices = [
			new(FP.FromRaw(step * 9 / 10), FP.Zero, FP.FromRaw(step * 9 / 10)),
			new(FP.FromRaw(step * 21 / 10), FP.Zero, FP.FromRaw(step * 19 / 10)),
			new(FP.FromRaw(step * 39 / 10), FP.Zero, FP.FromRaw(step * 29 / 10)),
			V(10, 0, 10), V(12, 0, 10), V(12, 0, 12),
		];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 3, 4, 5], [0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(report.OrientationFlips, Is.EqualTo(1));
			Assert.That(report.DegenerateTriangles, Is.EqualTo(1));
			Assert.That(mesh.TriangleCount, Is.EqualTo(1));
		});
	}

	[Test]
	public void CanonicallyEquivalentInputsProduceByteIdenticalMeshes() {
		var reference = NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP()).Mesh;

		// Reverse the vertex array, add an unused vertex and an exact duplicate, then reorder,
		// rotate, and re-wind the triangles.
		var original = TJunctionVertices();
		var vertices = new List<FVector3> { V(50, 0, 50) };
		vertices.AddRange(original.Reverse());
		vertices.Add(original[2]);
		int Map(int i) => i == 2 ? vertices.Count - 1 : original.Length - i;
		int[] shuffled = [
			Map(4), Map(2), Map(6),
			Map(1), Map(2), Map(0),
			Map(4), Map(5), Map(6),
			Map(3), Map(0), Map(2),
			Map(5), Map(4), Map(1),
		];

		var (mesh, report) = NavMeshBuilder.Build(vertices.ToArray(), shuffled, [0, 0, 0, 0, 0], 2.ToFP());

		Assert.That(report.WeldedVertices, Is.EqualTo(1));
		Assert.That(NavMeshBytes.Of(mesh), Is.EqualTo(NavMeshBytes.Of(reference)));
	}

	[Test]
	public void SharedEdgesAreReciprocalWithOppositePortals() {
		var mesh = NavMeshBuilder.Build(GridVertices(4), GridIndices(4), new int[32], 1.ToFP()).Mesh;

		AssertReciprocalPortals(mesh);
	}

	[Test]
	public void EveryTriangleIsFoundAtItsCentroid() {
		var mesh = NavMeshBuilder.Build(GridVertices(4), GridIndices(4), new int[32], FP.Half).Mesh;
		var query = new NavMeshQuery(mesh);

		for (var t = 0; t < mesh.TriangleCount; t++) {
			Assert.That(query.FindTriangle(mesh.Triangles[t].CenterXZ), Is.EqualTo(t));
		}
	}

	[Test]
	public void EdgeSharedByThreeTrianglesStaysOpen() {
		FVector3[] vertices = [V(0, 0, 0), V(4, 0, 0), V(2, 0, -3), V(2, 0, 3), V(1, 0, 2)];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 0, 1, 3, 0, 1, 4], [0, 0, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(report.NonManifoldEdges, Is.EqualTo(1));
			for (var t = 0; t < mesh.TriangleCount; t++) {
				Assert.That(NeighborCount(mesh, t), Is.Zero);
			}
		});
	}

	[Test]
	public void RepeatedTrianglesAreRemoved() {
		FVector3[] vertices = [V(0, 0, 0), V(4, 0, 0), V(4, 0, 4)];

		var (mesh, report) = NavMeshBuilder.Build(vertices, [0, 1, 2, 2, 1, 0], [3, 0], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(report.DuplicateTriangles, Is.EqualTo(1));
			Assert.That(mesh.TriangleCount, Is.EqualTo(1));
			Assert.That(mesh.Areas[0].AreaMask, Is.EqualTo(1), "the lowest area wins");
		});
	}

	[Test]
	public void AreaIndexBecomesTheAreaMaskBit() {
		var (mesh, _) = NavMeshBuilder.Build([V(0, 0, 0), V(4, 0, 0), V(4, 0, 4)], [0, 1, 2], [3], 1.ToFP());

		Assert.Multiple(() => {
			Assert.That(mesh.Areas[0].AreaMask, Is.EqualTo(8));
			Assert.That(mesh.Areas[0].CostMultiplier, Is.EqualTo(FP.One));
			Assert.That(mesh.Areas[0].IsBlocked, Is.False);
		});
	}

	[Test]
	public void ZonesSurviveTJunctionSplitsAndCanonicalization() {
		// Triangle 0, (0,0)-(4,0)-(4,4), is the one split by the T-junction vertex at (4,2).
		var result = NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], [5, -1, 2, 2, 2], 2.ToFP());
		var mesh = result.Mesh;

		Assert.That(result.TriangleZones, Has.Length.EqualTo(mesh.TriangleCount));
		Assert.Multiple(() => {
			for (var t = 0; t < mesh.TriangleCount; t++) {
				var center = mesh.Triangles[t].CenterXZ;
				var expected = center.X > 4.ToFP() ? 2 : center.Y < center.X ? 5 : -1;
				Assert.That(result.TriangleZones[t], Is.EqualTo(expected), $"triangle {t} at {center}");
			}
			Assert.That(result.TriangleZones.Count(static zone => zone == 5), Is.EqualTo(2), "both halves of the split triangle keep its zone");
			Assert.That(NavMeshBytes.Of(mesh), Is.EqualTo(NavMeshBytes.Of(NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP()).Mesh)),
				"zones do not change the mesh");
		});
	}

	[Test]
	public void WithoutZonesEveryTriangleIsInNone() {
		var result = NavMeshBuilder.Build(TJunctionVertices(), TJunctionIndices, [0, 0, 0, 0, 0], 2.ToFP());

		Assert.That(result.TriangleZones, Is.EqualTo(Enumerable.Repeat(-1, result.Mesh.TriangleCount)));
	}

	[Test]
	public void InvalidZonesAreRejected() {
		FVector3[] triangle = [V(0, 0, 0), V(4, 0, 0), V(4, 0, 4)];

		Assert.Multiple(() => {
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [0], [-2], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [0], [NavZoneData.MaxZones], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [0], [0, 0], FP.One), Throws.ArgumentException);
		});
	}

	[Test]
	public void InvalidInputIsRejected() {
		FVector3[] triangle = [V(0, 0, 0), V(4, 0, 0), V(4, 0, 4)];
		FVector3[] huge = [V(0, 0, 0), V(4, 0, 0), new(NavMeshBuilder.MaxCoordinate + FP.One, FP.Zero, FP.Zero)];
		FVector3[] collinear = [V(0, 0, 0), V(1, 0, 0), V(2, 0, 0)];

		Assert.Multiple(() => {
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1], [0], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 3], [0], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [0, 0], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [32], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(huge, [0, 1, 2], [0], FP.One), Throws.ArgumentException);
			Assert.That(() => NavMeshBuilder.Build(triangle, [0, 1, 2], [0], FP.Zero), Throws.InstanceOf<ArgumentOutOfRangeException>());
			Assert.That(() => NavMeshBuilder.Build(collinear, [0, 1, 2], [0], FP.One), Throws.InstanceOf<InvalidDataException>());
		});
	}

	[Test]
	public void GridCoversBoundsWhenTheCellSizeDoesNotDivideThem() {
		var (mesh, _) = NavMeshBuilder.Build([V(0, 0, 0), V(10, 0, 0), V(10, 0, 7)], [0, 1, 2], [0], R(1, 3));
		var query = new NavMeshQuery(mesh);

		Assert.That(query.FindTriangle(new FVector2(10.ToFP(), 7.ToFP())), Is.Zero);
	}

	/// <summary>
	/// Left square (two triangles) beside a right rectangle whose vertex 4 sits on the left
	/// square's edge (4,0)-(4,4).
	/// </summary>
	private static FVector3[] TJunctionVertices() {
		return [V(0, 0, 0), V(4, 0, 0), V(4, 0, 4), V(0, 0, 4), V(4, 0, 2), V(8, 0, 0), V(8, 0, 4)];
	}

	private static readonly int[] TJunctionIndices = [0, 1, 2, 0, 2, 3, 1, 5, 4, 5, 6, 4, 4, 6, 2];

	private static FVector3[] GridVertices(int size) {
		var vertices = new FVector3[(size + 1) * (size + 1)];
		for (var z = 0; z <= size; z++) {
			for (var x = 0; x <= size; x++) {
				vertices[z * (size + 1) + x] = V(x, 0, z);
			}
		}
		return vertices;
	}

	private static int[] GridIndices(int size) {
		var indices = new List<int>();
		for (var z = 0; z < size; z++) {
			for (var x = 0; x < size; x++) {
				var v00 = z * (size + 1) + x;
				var v10 = v00 + 1;
				var v01 = v00 + size + 1;
				var v11 = v01 + 1;
				indices.AddRange((x + z) % 2 == 0 ? [v00, v10, v11, v00, v11, v01] : [v00, v10, v01, v10, v11, v01]);
			}
		}
		return indices.ToArray();
	}

	private static void AssertReciprocalPortals(NavMesh mesh) {
		for (var t = 0; t < mesh.TriangleCount; t++) {
			var triangle = mesh.Triangles[t];
			for (var e = 0; e < 3; e++) {
				var neighbor = triangle.GetNeighbor(e);
				if (neighbor < 0) {
					continue;
				}
				triangle.GetPortal(e, out var left, out var right);
				var other = mesh.Triangles[neighbor];
				var back = Enumerable.Range(0, 3).Single(oe => other.GetNeighbor(oe) == t);
				other.GetPortal(back, out var otherLeft, out var otherRight);
				Assert.That((otherLeft, otherRight), Is.EqualTo((right, left)), $"triangle {t} edge {e} -> {neighbor}");
			}
		}
	}

	private static int NeighborCount(NavMesh mesh, int triangle) {
		var t = mesh.Triangles[triangle];
		return (t.Neighbor0 >= 0 ? 1 : 0) + (t.Neighbor1 >= 0 ? 1 : 0) + (t.Neighbor2 >= 0 ? 1 : 0);
	}

	private static FVector3 V(int x, int y, int z) {
		return NavMeshFixtures.V(x, y, z);
	}

	private static FP R(int numerator, int denominator) {
		return FP.FromRatio(numerator, denominator);
	}
}
