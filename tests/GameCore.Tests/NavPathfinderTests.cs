using Fixed64;
using NUnit.Framework;
using static Space.GameCore.Tests.NavMeshFixtures;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavPathfinderTests {
	private static readonly FVector3 s_ringStart = new(FP.Half, FP.Zero, FP.Half);
	private static readonly FVector3 s_ringEnd = new(FP.FromRatio(11, 2), FP.Zero, FP.FromRatio(11, 2));

	[Test]
	public void SameTriangleReturnsSingleTriangleCorridor() {
		var status = FindPath(Strip(), V(1, 0, 1), V(3, 0, 1), 1, out var corridor);

		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		Assert.That(corridor, Is.EqualTo(new[] { 0 }));
	}

	[Test]
	public void CorridorFollowsAdjacencyAcrossTheStrip() {
		var mesh = Strip();

		var across = FindPath(mesh, V(1, 0, 1), V(7, 0, 1), 1, out var acrossCorridor);
		var back = FindPath(mesh, V(1, 0, 3), V(5, 0, 3), 1, out var backCorridor);

		Assert.Multiple(() => {
			Assert.That(across, Is.EqualTo(NavPathStatus.Found));
			Assert.That(acrossCorridor, Is.EqualTo(new[] { 0, 3, 2 }));
			Assert.That(back, Is.EqualTo(NavPathStatus.Found));
			Assert.That(backCorridor, Is.EqualTo(new[] { 1, 0, 3 }));
		});
	}

	[Test]
	public void EndpointFailuresReportTheirCause() {
		var mesh = Strip();
		mesh.Areas[2].IsBlocked = true;
		mesh.Areas[1].AreaMask = 2;

		Assert.Multiple(() => {
			Assert.That(FindPath(mesh, V(-5, 0, 1), V(3, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.StartOffMesh));
			Assert.That(FindPath(mesh, V(3, 0, 1), V(9, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.EndOffMesh));
			Assert.That(FindPath(mesh, V(3, 0, 1), V(7, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.EndpointBlocked));
			Assert.That(FindPath(mesh, V(3, 0, 1), V(1, 0, 3), 1, out _), Is.EqualTo(NavPathStatus.EndAreaRejected));
		});
	}

	[Test]
	public void BlockedOrMaskedMiddleLeavesNoRoute() {
		var blocked = Strip();
		blocked.Areas[3].IsBlocked = true;
		var masked = Strip();
		masked.Areas[3].AreaMask = 2;

		Assert.Multiple(() => {
			Assert.That(FindPath(blocked, V(1, 0, 1), V(7, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.NoRoute));
			Assert.That(FindPath(masked, V(1, 0, 1), V(7, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.NoRoute));
		});
	}

	[Test]
	public void StartOnForbiddenGroundCanEscapeThroughItsOwnRegion() {
		var mesh = Strip();
		mesh.Areas[0].AreaMask = 2;
		mesh.Areas[3].AreaMask = 2;

		var status = FindPath(mesh, V(3, 0, 1), V(7, 0, 1), 1, out var corridor);

		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		Assert.That(corridor, Is.EqualTo(new[] { 0, 3, 2 }));
	}

	[Test]
	public void StartInABlockedRegionEscapesThroughIt() {
		var mesh = Strip();
		mesh.Areas[0].IsBlocked = true;
		mesh.Areas[3].IsBlocked = true;

		var status = FindPath(mesh, V(3, 0, 1), V(7, 0, 1), 1, out var corridor);

		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		Assert.That(corridor, Is.EqualTo(new[] { 0, 3, 2 }));
	}

	[Test]
	public void OpenGroundNeverEntersBlockedGround() {
		var mesh = Strip();
		mesh.Areas[1].IsBlocked = true;
		mesh.Areas[3].IsBlocked = true;

		// Blocked 1 may leave into open 0, but 0 may not re-enter blocked 3.
		Assert.That(FindPath(mesh, V(1, 0, 3), V(7, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.NoRoute));
	}

	[Test]
	public void AllowedGroundNeverEntersForbiddenGround() {
		var mesh = Strip();
		mesh.Areas[0].AreaMask = 2;

		Assert.That(FindPath(mesh, V(1, 0, 3), V(7, 0, 1), 1, out _), Is.EqualTo(NavPathStatus.NoRoute));
	}

	[Test]
	public void LongCorridorKeepsTheStartSide() {
		var mesh = Strip();
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		Span<int> corridor = stackalloc int[2];

		var status = pathfinder.FindPath(V(1, 0, 3), V(7, 0, 1), 1, corridor, out var length);

		Assert.That(status, Is.EqualTo(NavPathStatus.FoundTruncated));
		Assert.That(corridor[..length].ToArray(), Is.EqualTo(new[] { 1, 0 }));
	}

	[Test]
	public void IterationBudgetIsReportedSeparatelyFromNoRoute() {
		var pathfinder = new NavPathfinder(new NavMeshQuery(Strip()), new NavConfig(maxIterations: 1, maxPortals: 8));
		var corridor = new int[8];

		var status = pathfinder.FindPath(V(1, 0, 3), V(7, 0, 1), 1, corridor, out _);

		Assert.That(status, Is.EqualTo(NavPathStatus.IterationLimit));
	}

	[Test]
	public void EqualCostRoutesResolveByTriangleIndex() {
		var mesh = Ring();
		var expected = new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14, 15 };

		var first = FindPath(mesh, s_ringStart, s_ringEnd, 1, out var firstCorridor);
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var corridor = new int[32];
		for (var i = 0; i < 3; i++) {
			var status = pathfinder.FindPath(s_ringStart, s_ringEnd, 1, corridor, out var length);
			Assert.That(status, Is.EqualTo(NavPathStatus.Found));
			Assert.That(corridor[..length], Is.EqualTo(expected), $"repeat {i}");
		}

		Assert.That(first, Is.EqualTo(NavPathStatus.Found));
		Assert.That(firstCorridor, Is.EqualTo(expected));
	}

	[Test]
	public void CostMultiplierSteersAroundExpensiveGround() {
		var mesh = Ring();
		mesh.Areas[4].CostMultiplier = 10.ToFP();
		mesh.Areas[5].CostMultiplier = 10.ToFP();

		var status = FindPath(mesh, s_ringStart, s_ringEnd, 1, out var corridor);

		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		Assert.That(corridor, Is.EqualTo(new[] { 0, 1, 6, 7, 10, 11, 12, 13, 14, 15 }));
	}

	[Test]
	public void EveryCorridorStepCrossesASharedEdge() {
		var mesh = Ring();
		FindPath(mesh, s_ringStart, s_ringEnd, 1, out var corridor);

		for (var i = 0; i < corridor.Length - 1; i++) {
			var triangle = mesh.Triangles[corridor[i]];
			var adjacent = triangle.Neighbor0 == corridor[i + 1] || triangle.Neighbor1 == corridor[i + 1] || triangle.Neighbor2 == corridor[i + 1];
			Assert.That(adjacent, Is.True, $"{corridor[i]} -> {corridor[i + 1]}");
		}
	}

	private static NavPathStatus FindPath(NavMesh mesh, FVector3 start, FVector3 end, int areaMask, out int[] corridor) {
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var buffer = new int[64];
		var status = pathfinder.FindPath(start, end, areaMask, buffer, out var length);
		corridor = buffer[..length];
		return status;
	}
}
