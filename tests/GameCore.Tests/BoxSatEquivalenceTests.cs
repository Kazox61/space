using Fixed;
using Fixed32;
using NUnit.Framework;

namespace Space.GameCore.Tests;

public sealed class BoxSatEquivalenceTests {
	private static FWorldTransform Pose(FVector3 position, FQuaternion rotation) => new(new FPos(position.X.To64(), position.Y.To64(), position.Z.To64()), rotation);
	private static void Equal(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB, string label) {
		var expected = BoxSatReference.Collide(a, xfA, b, xfB);
		var actual = Manifold.Collide(a, xfA, b, xfB);
		// Compare every raw fixed-point value and field, including unused point slots and point order.
		Assert.That(actual.Normal, Is.EqualTo(expected.Normal), label + " normal");
		Assert.That(actual.PointCount, Is.EqualTo(expected.PointCount), label + " count");
		for (var p = 0; p < Manifold.MaxPoints; p++)
			Assert.That(actual.GetPoint(p), Is.EqualTo(expected.GetPoint(p)), label + $" point {p}");
		var xfBinA = FWorldTransform.InvMul(xfA, xfB);
		var centerB = FTransform.TransformPoint(xfBinA, b.HullShape.Center);
		var rotationB = xfBinA.Rotation * b.HullShape.Rotation;
		var inverseA = FQuaternion.Inverse(a.HullShape.Rotation);
		var localCenter = inverseA * (centerB - a.HullShape.Center);
		var localRotation = inverseA * rotationB;
		Assert.That(BoxSatReference.OptimizedEdge(a.HullShape.HalfExtents, localCenter, localRotation, b.HullShape.HalfExtents),
			Is.EqualTo(BoxSatReference.QueryEdge(a.HullShape.HalfExtents, localCenter, localRotation, b.HullShape.HalfExtents)), label + " edge separation and winning indices (even when face wins)");
	}

	[Test]
	public void LargeGroundRegressionsMatchOriginalInBothOrders() {
		var ground = Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP()));
		var crate = Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Half, FP.Half));
		var rotation = FQuaternion.Normalize(new FQuaternion(0.07.ToFP(), -0.16.ToFP(), -0.98.ToFP(), -0.07.ToFP()));
		foreach (var x in new[] { 34.2556, 34.26, 0, -34.26 })
			foreach (var z in new[] { -33.1656, -33.17, 0, 33.17 })
				foreach (var y in new[] { 0.8997, 0.9, 1.9 }) {
					var xf = Pose(new FVector3(x.ToFP(), y.ToFP(), z.ToFP()), rotation);
					Equal(ground, FWorldTransform.Identity, crate, xf, $"ground ({x},{y},{z})");
					Equal(crate, xf, ground, FWorldTransform.Identity, $"crate ({x},{y},{z})");
				}
	}

	[Test]
	public void DeterministicPoseCorpusMatchesOriginalInBothOrders() {
		uint seed = 0xB05A7;
		int Next(int min, int max) {
			seed = unchecked(seed * 1664525 + 1013904223);
			return min + (int)(seed % (uint)(max - min));
		}
		FVector3 Vector(int min, int max, int denominator) => new(FP.FromRatio(Next(min, max), denominator), FP.FromRatio(Next(min, max), denominator), FP.FromRatio(Next(min, max), denominator));
		FQuaternion Rotation(int index) => (index % 8) switch {
			0 => FQuaternion.Identity,
			1 => FQuaternion.AxisAngleDegrees(FVector3.Up, FP.FromRaw(Next(-32, 33))),
			2 => FQuaternion.AxisAngleDegrees(FVector3.Right, 90.ToFP() + FP.FromRaw(Next(-32, 33))),
			_ => FQuaternion.Normalize(new FQuaternion(FP.FromRatio(Next(-1000, 1001), 1000), FP.FromRatio(Next(-1000, 1001), 1000), FP.FromRatio(Next(-1000, 1001), 1000), FP.One)),
		};
		var counts = new int[Manifold.MaxPoints + 1];
		for (var i = 0; i < 4096; i++) {
			var a = Shape.MakeBox(Vector(-20, 21, 100), Vector(5, 301, 100));
			var b = Shape.MakeBox(Vector(-20, 21, 100), Vector(5, 301, 100));
			a.HullShape.Rotation = Rotation(i);
			b.HullShape.Rotation = Rotation(i + 3);
			var basePosition = Vector(-4000, 4001, 1);
			var xfA = Pose(basePosition, Rotation(i + 1));
			var xfB = Pose(basePosition + Vector(-600, 601, 100), Rotation(i + 2));
			Equal(a, xfA, b, xfB, $"pose {i} A/B");
			Equal(b, xfB, a, xfA, $"pose {i} B/A");
			counts[Manifold.Collide(a, xfA, b, xfB).PointCount]++;
		}
		TestContext.WriteLine("Corpus point counts: " + string.Join(", ", counts));
		Assert.That(counts[0], Is.GreaterThan(0), "separated pairs");
		Assert.That(counts[1], Is.GreaterThan(0), "single-point contacts");
		Assert.That(counts[4], Is.GreaterThan(0), "four-point contacts");
	}

	[Test]
	public void TouchingAndSpeculativeBoundariesMatchOriginal() {
		var box = Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Half, FP.Half));
		foreach (var axis in new[] { FVector3.Right, FVector3.Up, FVector3.Forward })
			foreach (var offset in new[] { -B3Config.LinearSlop, FP.Zero, B3Config.SpeculativeDistance })
				foreach (var raw in new[] { -1, 0, 1 }) {
					var xf = Pose((FP.One + offset + FP.FromRaw(raw)) * axis, FQuaternion.Identity);
					Equal(box, FWorldTransform.Identity, box, xf, $"boundary {axis} {offset} {raw}");
					Equal(box, xf, box, FWorldTransform.Identity, $"reverse boundary {axis} {offset} {raw}");
				}
	}

	[Test]
	public void NearParallelAndGaussMapBoundariesMatchOriginalEdgeQueries() {
		var he = new FVector3(FP.Half, FP.One, FP.FromRatio(3, 2));
		var accepted = 0;
		// Sweep fixed-point steps across parallel and small-angle acceptance boundaries, plus
		// symmetric candidate ties. Compare winning indices even if no edge contact is built.
		foreach (var axis in new[] { FVector3.Up, FVector3.NormalizeSafe(new FVector3(FP.One, FP.One, FP.One)) })
			foreach (var angle in new[] { FP.Zero, FP.FromRatio(2865, 10000), FP.FromRatio(574, 100), 45.ToFP(), 90.ToFP() })
				for (var raw = -32; raw <= 32; raw++) {
					var rotation = FQuaternion.AxisAngleDegrees(axis, angle + FP.FromRaw(raw));
					var center = new FVector3(FP.Half, FP.Half, FP.Half);
					var original = BoxSatReference.QueryEdge(he, center, rotation, he);
					Assert.That(BoxSatReference.OptimizedEdge(he, center, rotation, he), Is.EqualTo(original), $"axis={axis} angle={angle} raw={raw}");
					if (original.EdgeA >= 0)
						accepted++;
				}
		Assert.That(accepted, Is.GreaterThan(0));
	}
}
