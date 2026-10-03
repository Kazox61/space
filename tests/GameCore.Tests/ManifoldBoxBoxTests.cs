using Fixed;
using Fixed32;
using NUnit.Framework;

namespace Space.GameCore.Tests;

/// <summary>
/// Narrow-phase regressions for box-versus-box deep overlap, from a real failure: a crate knocked
/// over by a player and pushed along the 80 x 1 x 80 ground wedged into the surface while tilted
/// ~160 degrees. The edge SAT query measured the line-to-line distance of the two edges instead of
/// the boxes' separation along the edge axis, so a far-away ground edge "separated" the overlapping
/// pair, the manifold came back empty, and the crate sank through the ground at full gravity.
/// </summary>
public sealed class ManifoldBoxBoxTests {
	// A pose based on the sinking crate: overlapping the ground's top surface by
	// ~0.24 while tilted past horizontal, near the ground's +X edge.
	private static FWorldTransform CrateAt(double x, double y, double z) {
		var rotation = FQuaternion.Normalize(new FQuaternion(0.07.ToFP(), -0.16.ToFP(), -0.98.ToFP(), -0.07.ToFP()));
		return new FWorldTransform(new FPos(x.ToFP().To64(), y.ToFP().To64(), z.ToFP().To64()), rotation);
	}

	private static Shape Ground() => Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP()));

	private static Shape Crate() => Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Half, FP.Half));

	[Test]
	public void TiltedCrateOverlappingTheGroundGetsAManifoldInBothOrders() {
		var groundXf = new FWorldTransform(FPos.Zero, FQuaternion.Identity);
		var crateXf = CrateAt(34.2556, 0.8997, -33.1656);

		var groundFirst = Manifold.Collide(Ground(), groundXf, Crate(), crateXf);
		var crateFirst = Manifold.Collide(Crate(), crateXf, Ground(), groundXf);

		Assert.Multiple(() => {
			Assert.That(groundFirst.PointCount, Is.GreaterThanOrEqualTo(1), "ground as A must report the overlap");
			Assert.That(groundFirst.MinSeparation(), Is.LessThan(FP.Zero));
			Assert.That(crateFirst.PointCount, Is.GreaterThanOrEqualTo(1), "ground as B must report the overlap");
			Assert.That(crateFirst.MinSeparation(), Is.LessThan(FP.Zero));
		});
	}

	[TestCase(34.26, 0.90, -33.17)]
	[TestCase(0, 0.90, 0)]
	[TestCase(-34.26, 0.90, 33.17)]
	[TestCase(34.26, 0.90, 33.17)]
	public void OverlapIsDetectedAcrossTheWholeGround(double x, double y, double z) {
		var manifold = Manifold.Collide(Ground(), new FWorldTransform(FPos.Zero, FQuaternion.Identity), Crate(), CrateAt(x, y, z));

		Assert.That(manifold.PointCount, Is.GreaterThanOrEqualTo(1), $"crate at ({x}, {y}, {z}) overlaps the ground");
	}

	[Test]
	public void ACrateAboveTheSurfaceStillReportsNoContact() {
		var manifold = Manifold.Collide(Ground(), new FWorldTransform(FPos.Zero, FQuaternion.Identity), Crate(), CrateAt(34.26, 1.90, -33.17));

		Assert.That(manifold.PointCount, Is.EqualTo(0));
	}
}
