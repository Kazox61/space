using Fixed;
using Fixed32;
using NUnit.Framework;
using Space.NavBuilder;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavSourceTriangulatorTests {
	[Test]
	public void BoxProducesTransformedVerticesAndOutwardTriangles() {
		var box = Box("Map/Box", 10, NavContribution.Walkable);

		var soup = NavSourceTriangulator.Build([box]);

		Assert.Multiple(() => {
			Assert.That(soup.Vertices, Has.Count.EqualTo(8));
			Assert.That(soup.Indices, Has.Count.EqualTo(36));
			Assert.That(soup.TriangleContributions, Has.Count.EqualTo(12));
			Assert.That(soup.Vertices[0], Is.EqualTo(new Fixed64.FVector3(F64(8), F64(17), F64(26))));
			Assert.That(soup.Vertices[6], Is.EqualTo(new Fixed64.FVector3(F64(12), F64(23), F64(34))));
			Assert.That(soup.TriangleContributions, Is.All.EqualTo(NavContribution.Walkable));
		});

		AssertOutwardNormals(soup, FQuaternion.Identity);
	}

	private static void AssertOutwardNormals(NavTriangleSoup soup, FQuaternion rotation) {
		var expectedNormalDirections = new[] {
			(rotation * FVector3.Down).To64(),
			(rotation * FVector3.Up).To64(),
			(rotation * FVector3.Backward).To64(),
			(rotation * FVector3.Forward).To64(),
			(rotation * FVector3.Left).To64(),
			(rotation * FVector3.Right).To64(),
		};
		for (var face = 0; face < expectedNormalDirections.Length; face++) {
			for (var faceTriangle = 0; faceTriangle < 2; faceTriangle++) {
				var triangle = face * 6 + faceTriangle * 3;
				var a = soup.Vertices[soup.Indices[triangle]];
				var b = soup.Vertices[soup.Indices[triangle + 1]];
				var c = soup.Vertices[soup.Indices[triangle + 2]];
				var normal = Fixed64.FVector3.Cross(b - a, c - a);
				Assert.That(Fixed64.FVector3.Dot(normal, expectedNormalDirections[face]), Is.GreaterThan(Fixed64.FP.Zero));
			}
		}
	}

	[Test]
	public void BuildSortsBoxesAndExcludesDisabledGeometry() {
		var soup = NavSourceTriangulator.Build([
			Box("Map/B", 20, NavContribution.ObstacleOnly),
			Box("Map/Excluded", 30, NavContribution.Excluded),
			Box("Map/A", 10, NavContribution.Walkable),
		]);

		Assert.Multiple(() => {
			Assert.That(soup.Vertices, Has.Count.EqualTo(16));
			Assert.That(soup.Vertices[0].X, Is.EqualTo(F64(8)));
			Assert.That(soup.Vertices[8].X, Is.EqualTo(F64(18)));
			Assert.That(soup.TriangleContributions.Take(12), Is.All.EqualTo(NavContribution.Walkable));
			Assert.That(soup.TriangleContributions.Skip(12), Is.All.EqualTo(NavContribution.ObstacleOnly));
		});
	}

	[Test]
	public void BuildAppliesColliderRotation() {
		var box = Box("Map/Rotated", 10, NavContribution.Walkable) with {
			Transform = new FWorldTransform(
				new FPos(F64(10), F64(20), F64(30)),
				FQuaternion.AxisAngleDegrees(FVector3.Up, 45.ToFP())
			)
		};
		var localCorner = new FVector3(-2.ToFP(), -3.ToFP(), -4.ToFP());
		var rotation = FQuaternion.Normalize(box.Transform.Rotation);
		var expected = box.Transform.Position + rotation * localCorner;

		var soup = NavSourceTriangulator.Build([box]);

		Assert.That(soup.Vertices[0], Is.EqualTo(new Fixed64.FVector3(expected.X, expected.Y, expected.Z)));
		AssertOutwardNormals(soup, rotation);
	}

	[Test]
	public void BuildRejectsDuplicateSourcePaths() {
		Assert.That(() => NavSourceTriangulator.Build([
			Box("Map/Box", 10, NavContribution.Walkable),
			Box("Map/Box", 20, NavContribution.ObstacleOnly),
		]), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Box"));
	}

	[Test]
	public void BuildRejectsInvalidColliderGeometry() {
		var box = Box("Map/Box", 10, NavContribution.Walkable) with {
			HalfExtents = new FVector3(FP.Zero, 3.ToFP(), 4.ToFP())
		};

		Assert.That(() => NavSourceTriangulator.Build([box]),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Box"));
	}

	[Test]
	public void BuildNormalizesRotationLikePhysics() {
		var box = Box("Map/Box", 10, NavContribution.Walkable) with {
			Transform = new FWorldTransform(
				new FPos(F64(10), F64(20), F64(30)),
				new FQuaternion(FP.Zero, FP.Zero, FP.Zero, FP.Two)
			)
		};

		var soup = NavSourceTriangulator.Build([box]);

		Assert.That(soup.Vertices[0], Is.EqualTo(new Fixed64.FVector3(F64(8), F64(17), F64(26))));
		AssertOutwardNormals(soup, FQuaternion.Identity);
	}

	private static NavSourceBox Box(string path, int x, NavContribution navigation) => new(
		path,
		new FWorldTransform(new FPos(F64(x), F64(20), F64(30)), FQuaternion.Identity),
		new FVector3(2.ToFP(), 3.ToFP(), 4.ToFP()),
		navigation
	);

	private static Fixed64.FP F64(int value) => Fixed64.FP.FromRatio(value, 1);
}
