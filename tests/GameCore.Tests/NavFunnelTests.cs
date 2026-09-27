using Fixed64;
using NUnit.Framework;
using static Space.GameCore.Tests.NavMeshFixtures;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavFunnelTests {
	private static readonly FVector3 s_ringStart = new(FP.Half, FP.Zero, FP.Half);
	private static readonly FVector3 s_ringEnd = new(FP.FromRatio(11, 2), FP.Zero, FP.FromRatio(11, 2));

	[Test]
	public void SingleTriangleCorridorIsAStraightLine() {
		var funnel = new NavFunnel(Strip(), NavConfig.Default);
		var waypoints = new FVector3[8];

		var count = funnel.FindPath([0], V(1, 0, 1), V(3, 0, 1), waypoints);

		Assert.That(waypoints[..count], Is.EqualTo(new[] { V(1, 0, 1), V(3, 0, 1) }));
	}

	[Test]
	public void StraightCorridorHasNoCorners() {
		var funnel = new NavFunnel(Strip(), NavConfig.Default);
		var waypoints = new FVector3[8];

		var count = funnel.FindPath([0, 3, 2], V(1, 0, 1), V(7, 0, 1), waypoints);

		Assert.That(waypoints[..count], Is.EqualTo(new[] { V(1, 0, 1), V(7, 0, 1) }));
	}

	[Test]
	public void PathBendsExactlyAtTheObstacleCorner() {
		var mesh = Ring();
		var corridor = FindCorridor(mesh);
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var waypoints = new FVector3[8];

		var count = funnel.FindPath(corridor, s_ringStart, s_ringEnd, waypoints);

		Assert.That(waypoints[..count], Is.EqualTo(new[] { s_ringStart, V(4, 0, 2), s_ringEnd }));
	}

	[Test]
	public void OtherSideOfTheObstacleBendsAtTheMirroredCorner() {
		var mesh = Ring();
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var waypoints = new FVector3[8];

		var count = funnel.FindPath([0, 1, 6, 7, 10, 11, 12, 13, 14, 15], s_ringStart, s_ringEnd, waypoints);

		Assert.That(waypoints[..count], Is.EqualTo(new[] { s_ringStart, V(2, 0, 4), s_ringEnd }));
	}

	[Test]
	public void ClockwiseMeshProducesTheSameCorners() {
		var counterClockwise = Ring();
		var clockwise = Ring(clockwise: true);
		var corridor = FindCorridor(counterClockwise);
		var expected = new FVector3[8];
		var actual = new FVector3[8];

		var expectedCount = new NavFunnel(counterClockwise, NavConfig.Default).FindPath(corridor, s_ringStart, s_ringEnd, expected);
		var actualCount = new NavFunnel(clockwise, NavConfig.Default).FindPath(corridor, s_ringStart, s_ringEnd, actual);

		Assert.That(actual[..actualCount], Is.EqualTo(expected[..expectedCount]));
	}

	[Test]
	public void FindCornersSkipsTheStartAndEndsAtTheTarget() {
		var mesh = Ring();
		var corridor = FindCorridor(mesh);
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var corners = new FVector3[4];

		var count = funnel.FindCorners(corridor, s_ringStart, s_ringEnd, corners);

		Assert.That(corners[..count], Is.EqualTo(new[] { V(4, 0, 2), s_ringEnd }));
	}

	[Test]
	public void FindCornersRespectsTheOutputCapacity() {
		var mesh = Ring();
		var corridor = FindCorridor(mesh);
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var corners = new FVector3[1];

		var count = funnel.FindCorners(corridor, s_ringStart, s_ringEnd, corners);

		Assert.That(corners[..count], Is.EqualTo(new[] { V(4, 0, 2) }));
	}

	[Test]
	public void FindPathKeepsTheEndWhenTheOutputIsTight() {
		var mesh = Ring();
		var corridor = FindCorridor(mesh);
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var waypoints = new FVector3[2];

		var count = funnel.FindPath(corridor, s_ringStart, s_ringEnd, waypoints);

		Assert.That(waypoints[..count], Is.EqualTo(new[] { s_ringStart, s_ringEnd }));
	}

	[Test]
	public void FindCornersDropsACornerTheAgentIsStandingOn() {
		var mesh = Ring();
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var corners = new FVector3[4];
		var atCorner = V(4, 0, 2);

		// Cell (2,0) lower-left triangle 4 touches the hole corner at (4, 2).
		var count = funnel.FindCorners([4, 5, 8, 9, 14, 15], atCorner, s_ringEnd, corners);

		Assert.That(corners[..count], Is.EqualTo(new[] { s_ringEnd }));
	}

	[Test]
	public void RepeatedRunsProduceIdenticalWaypoints() {
		var mesh = Ring();
		var corridor = FindCorridor(mesh);
		var funnel = new NavFunnel(mesh, NavConfig.Default);
		var first = new FVector3[8];
		var again = new FVector3[8];

		var firstCount = funnel.FindPath(corridor, s_ringStart, s_ringEnd, first);
		funnel.FindCorners(corridor, s_ringStart, s_ringEnd, new FVector3[4]);
		var againCount = funnel.FindPath(corridor, s_ringStart, s_ringEnd, again);

		Assert.That(again[..againCount], Is.EqualTo(first[..firstCount]));
	}

	private static int[] FindCorridor(NavMesh mesh) {
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var corridor = new int[32];
		var status = pathfinder.FindPath(s_ringStart, s_ringEnd, 1, corridor, out var length);
		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		return corridor[..length];
	}
}
