using Fixed64;
using NUnit.Framework;
using static Space.GameCore.Tests.NavMeshFixtures;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavMeshQueryTests {
	[Test]
	public void FindTriangleResolvesInteriorPointsAndRejectsOffMeshPoints() {
		var query = new NavMeshQuery(Strip());

		Assert.Multiple(() => {
			Assert.That(query.FindTriangle(XZ(3, 1)), Is.EqualTo(0));
			Assert.That(query.FindTriangle(XZ(1, 3)), Is.EqualTo(1));
			Assert.That(query.FindTriangle(XZ(7, 1)), Is.EqualTo(2));
			Assert.That(query.FindTriangle(XZ(5, 3)), Is.EqualTo(3));
			Assert.That(query.FindTriangle(XZ(-1, 1)), Is.EqualTo(-1));
			Assert.That(query.FindTriangle(XZ(9, 1)), Is.EqualTo(-1));
		});
	}

	[Test]
	public void FindTriangleOnSharedEdgeReturnsLowerIndex() {
		var query = new NavMeshQuery(Strip());

		Assert.That(query.FindTriangle(XZ(2, 2)), Is.EqualTo(0));
	}

	[Test]
	public void FindTriangleOnFarBoundsEdgeClampsIntoLastCell() {
		var query = new NavMeshQuery(Strip());

		Assert.Multiple(() => {
			Assert.That(query.FindTriangle(XZ(8, 2)), Is.EqualTo(2));
			Assert.That(query.FindTriangle(XZ(6, 4)), Is.EqualTo(3));
		});
	}

	[Test]
	public void SampleHeightInterpolatesAndClampsToVertexRange() {
		var query = new NavMeshQuery(Slope());

		Assert.Multiple(() => {
			Assert.That(query.SampleHeight(XZ(0, 0), 0), Is.EqualTo(FP.Zero));
			Assert.That(query.SampleHeight(XZ(4, 0), 0), Is.EqualTo(2.ToFP()));
			Assert.That(query.SampleHeight(XZ(2, 1), 0), Is.EqualTo(FP.One));
			Assert.That(query.SampleHeight(XZ(8, 1), 0), Is.EqualTo(2.ToFP()));
		});
	}

	[Test]
	public void FindTriangleWithHeightPicksNearestFloor() {
		var query = new NavMeshQuery(TwoFloors());

		Assert.Multiple(() => {
			Assert.That(query.FindTriangle(XZ(3, 1), FP.Zero), Is.EqualTo(0));
			Assert.That(query.FindTriangle(XZ(3, 1), 3.ToFP()), Is.EqualTo(2));
			Assert.That(query.FindTriangle(XZ(3, 1), 2.ToFP()), Is.EqualTo(2));
			Assert.That(query.FindTriangle(XZ(3, 1), FP.One), Is.EqualTo(0));
		});
	}

	[Test]
	public void EndpointLookupPrefersPassableTriangleOnSameSurface() {
		var mesh = Strip();
		mesh.Areas[2].AreaMask = 2;
		var query = new NavMeshQuery(mesh);
		var sharedEdgePoint = XZ(6, 2);

		Assert.Multiple(() => {
			Assert.That(query.FindTriangle(sharedEdgePoint, FP.Zero), Is.EqualTo(2));
			Assert.That(query.FindTriangleForEndpoint(sharedEdgePoint, FP.Zero, 1), Is.EqualTo(3));
			Assert.That(query.FindPassableTriangleForEndpoint(sharedEdgePoint, FP.Zero, 1), Is.EqualTo(3));
			Assert.That(query.FindPassableTriangleForEndpoint(XZ(7, 1), FP.Zero, 1), Is.EqualTo(-1));
		});
	}

	[Test]
	public void EndpointTieBreakNeverSwapsFloors() {
		var mesh = TwoFloors();
		mesh.Areas[0].AreaMask = 2;
		mesh.Areas[1].AreaMask = 2;
		var query = new NavMeshQuery(mesh);
		var midway = FP.FromRatio(3, 2);

		Assert.That(query.FindTriangleForEndpoint(XZ(3, 1), midway, 1), Is.EqualTo(0));
	}

	[Test]
	public void FindPassableTriangleSkipsBlockedAndMaskedTriangles() {
		var mesh = TwoFloors();
		mesh.Areas[0].IsBlocked = true;
		var query = new NavMeshQuery(mesh);

		Assert.Multiple(() => {
			Assert.That(query.FindPassableTriangle(XZ(3, 1), 1), Is.EqualTo(2));
			Assert.That(query.FindPassableTriangle(XZ(3, 1), 2), Is.EqualTo(-1));
		});
	}

	[Test]
	public void ClosestPointReturnsInputOnMeshAndNearestEdgePointOffMesh() {
		var query = new NavMeshQuery(Strip());

		var inside = query.ClosestPoint(XZ(3, 1), out var insideTriangle);
		var outside = query.ClosestPoint(XZ(10, 2), out var outsideTriangle);

		Assert.Multiple(() => {
			Assert.That(inside, Is.EqualTo(XZ(3, 1)));
			Assert.That(insideTriangle, Is.EqualTo(0));
			Assert.That(outside, Is.EqualTo(XZ(8, 2)));
			Assert.That(outsideTriangle, Is.EqualTo(2));
		});
	}

	[Test]
	public void ClosestPointResultsAreFoundByTriangleLookup() {
		var mesh = SampleMesh();
		var query = new NavMeshQuery(mesh);
		var offsets = new[] { FP.FromRaw(1), FP.FromRatio(1, 100000), FP.FromRatio(1, 10000), FP.FromRatio(1, 1000) };
		var failures = new List<string>();

		void Check(FVector2 point) {
			var closest = query.ClosestPoint(point, out var triangle);
			if (triangle >= 0 && query.FindTriangle(closest) < 0) {
				failures.Add($"({closest.X.RawValue}, {closest.Y.RawValue}) from triangle {triangle}");
			}
			var passable = query.ClosestPassablePoint(point, ~0, out triangle);
			if (triangle >= 0 && query.FindPassableTriangle(passable, ~0) < 0) {
				failures.Add($"passable ({passable.X.RawValue}, {passable.Y.RawValue}) from triangle {triangle}");
			}
		}

		// Where the chasing character froze: just past the z=-3.25 mesh edge, which is also a grid line.
		Check(new FVector2(FP.FromRaw(-3114598400), FP.FromRaw(-6979354624)));
		// Points just inside and just outside every edge, where the point-in-triangle tolerance applies.
		foreach (ref readonly var t in mesh.Triangles) {
			for (var e = 0; e < 3; e++) {
				t.GetEdgeVertices(e, out var va, out var vb);
				var a = mesh.GetVertexXZ(va);
				var b = mesh.GetVertexXZ(vb);
				var normal = FVector2.NormalizeSafe(new FVector2(b.Y - a.Y, a.X - b.X));
				for (var step = 1; step < 10; step++) {
					var onEdge = a + (b - a) * FP.FromRatio(step, 10);
					foreach (var offset in offsets) {
						Check(onEdge + normal * offset);
						Check(onEdge - normal * offset);
					}
				}
			}
		}

		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void ClosestPassablePointAvoidsForbiddenGround() {
		var mesh = Strip();
		mesh.Areas[2].IsBlocked = true;
		var query = new NavMeshQuery(mesh);

		var point = query.ClosestPassablePoint(XZ(7, 1), 1, out var triangle);

		Assert.Multiple(() => {
			Assert.That(point, Is.EqualTo(XZ(6, 2)));
			Assert.That(triangle, Is.EqualTo(3));
		});
	}

	[Test]
	public void ProjectFailsBeyondMaxDistance() {
		var query = new NavMeshQuery(Strip());

		var near = query.Project(XZ(9, 2), 2.ToFP(), out var nearTriangle);
		var far = query.Project(XZ(9, 2), FP.Half, out var farTriangle);

		Assert.Multiple(() => {
			Assert.That(near, Is.EqualTo(XZ(8, 2)));
			Assert.That(nearTriangle, Is.EqualTo(2));
			Assert.That(far, Is.EqualTo(XZ(9, 2)));
			Assert.That(farTriangle, Is.EqualTo(-1));
		});
	}

	[Test]
	public void GeometryPrimitivesHandleEdgesAndDegenerateInput() {
		var a = XZ(0, 0);
		var b = XZ(4, 0);
		var c = XZ(0, 4);

		NavGeometry.Barycentric(XZ(1, 1), a, b, c, out var u, out var v, out var w);
		NavGeometry.Barycentric(XZ(1, 1), a, a, a, out var du, out var dv, out var dw);

		Assert.Multiple(() => {
			Assert.That(NavGeometry.PointInTriangle(XZ(2, 0), a, b, c), Is.True);
			Assert.That(NavGeometry.PointInTriangle(XZ(0, 0), a, b, c), Is.True);
			Assert.That(NavGeometry.PointInTriangle(XZ(3, 3), a, b, c), Is.False);
			Assert.That(NavGeometry.PointInTriangle(XZ(2, 2), a, c, b), Is.True, "winding independent");
			Assert.That(u + v + w, Is.EqualTo(FP.One));
			Assert.That(v, Is.EqualTo(FP.Quarter));
			Assert.That(w, Is.EqualTo(FP.Quarter));
			Assert.That(du, Is.EqualTo(dv).And.EqualTo(dw));
			Assert.That(NavGeometry.ClosestPointOnSegment(XZ(-2, 3), a, b), Is.EqualTo(a));
			Assert.That(NavGeometry.ClosestPointOnSegment(XZ(2, 3), a, b), Is.EqualTo(XZ(2, 0)));
			Assert.That(NavGeometry.ClosestPointOnSegment(XZ(2, 3), a, a), Is.EqualTo(a));
			Assert.That(NavGeometry.SignedArea(a, b, c), Is.EqualTo(8.ToFP()));
			Assert.That(NavGeometry.SignedArea(a, c, b), Is.EqualTo(-8.ToFP()));
		});
	}

	[Test]
	public void MeshRejectsNonReciprocalAdjacency() {
		FVector3[] vertices = [V(0, 0, 0), V(4, 0, 0), V(0, 0, 4), V(4, 0, 4)];
		NavTriangle[] triangles = [
			NavTriangle.Create(vertices, 0, 1, 3, -1, -1, 1),
			NavTriangle.Create(vertices, 0, 3, 2, -1, -1, -1),
		];

		Assert.That(
			() => new NavMesh(vertices, triangles, [NavTriangleArea.Default, NavTriangleArea.Default],
				new FAABB2(XZ(0, 0), XZ(4, 4)), [0, 2], [0, 1], 1, 1, 4.ToFP(), XZ(0, 0)),
			Throws.ArgumentException.With.Message.Contains("does not share that edge back"));
	}

	[Test]
	public void MeshRejectsGridThatDoesNotCoverBounds() {
		FVector3[] vertices = [V(0, 0, 0), V(4, 0, 0), V(0, 0, 4)];
		NavTriangle[] triangles = [NavTriangle.Create(vertices, 0, 1, 2, -1, -1, -1)];

		Assert.That(
			() => new NavMesh(vertices, triangles, [NavTriangleArea.Default],
				new FAABB2(XZ(0, 0), XZ(4, 4)), [0, 1], [0], 1, 1, 2.ToFP(), XZ(0, 0)),
			Throws.ArgumentException.With.Message.Contains("cover"));
	}

	/// <summary>The navmesh shipped in the committed sample level.</summary>
	private static NavMesh SampleMesh() {
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent) {
			var candidate = Path.Combine(directory.FullName, "Client", "maps", "level_pipeline_test.level.bytes");
			if (File.Exists(candidate)) {
				return LevelFile.ReadFromDisk(candidate).Data.Navigation!.Mesh.CreateMesh();
			}
		}
		throw new FileNotFoundException("Could not find the sample level file.");
	}
}
