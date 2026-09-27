using FFS.Libraries.StaticEcs;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct NavDebugDrawWorldA : IWorldType, ISessionType;

// Hash-compared worlds, each created once per process (see NavRollbackTests).
public struct NavDebugUndrawnWorld : IWorldType, ISessionType;

public struct NavDebugDrawnWorld : IWorldType, ISessionType;

/// <summary>
/// Phase 7: the navigation overlay on the committed sample level, whose obstacle-only test box
/// covers x in [-2, 2], z in [-8, -4] and was baked with agent radius 0.5.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class NavDebugDrawTests {
	private const double BoxMinX = -2, BoxMaxX = 2, BoxMinZ = -8, BoxMaxZ = -4;
	private const double AgentRadius = 0.5;
	private const int Ticks = 240;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<NavDebugDrawWorldA>();
		DestroyIfCreated<NavDebugUndrawnWorld>();
		DestroyIfCreated<NavDebugDrawnWorld>();
	}

	[Test]
	public void OverlayShowsWhyTheSampleObstacleIsBlocked() {
		CreateWorld<NavDebugDrawWorldA>(Level());
		var draw = new Recorder();
		new Core<NavDebugDrawWorldA>.NavDebugDraw(Level()).Draw(draw, NavDebugDrawFlags.Triangles | NavDebugDrawFlags.Edges | NavDebugDrawFlags.Sources);

		var mesh = Level().Navigation!.Mesh;
		var clearance = draw.Segments(NavDebugColor.ObstacleClearance);
		// Boundary edges that ring the obstacle: outside its footprint grown by the radius, within one unit of it.
		var ring = draw.Segments(NavDebugColor.BoundaryEdge).Count(s => {
			var (x, z) = Midpoint(s);
			return Inside(x, z, BoxMinX - AgentRadius - 1, BoxMaxX + AgentRadius + 1, BoxMinZ - AgentRadius - 1, BoxMaxZ + AgentRadius + 1)
				&& !Inside(x, z, BoxMinX - AgentRadius, BoxMaxX + AgentRadius, BoxMinZ - AgentRadius, BoxMaxZ + AgentRadius);
		});
		var walkableInsideClearance = draw.Triangles.Count(t => t.Color == NavDebugColor.WalkableArea
			&& Inside(Centroid(t).X, Centroid(t).Z, BoxMinX - AgentRadius, BoxMaxX + AgentRadius, BoxMinZ - AgentRadius, BoxMaxZ + AgentRadius));

		Assert.Multiple(() => {
			Assert.That(draw.Triangles, Has.Count.EqualTo(mesh.TriangleCount), "every shipped triangle is drawn");
			Assert.That(draw.Triangles.Select(t => t.Color).Distinct(), Is.EqualTo(new[] { NavDebugColor.WalkableArea }), "the sample ships one walkable area");
			Assert.That(draw.Segments(NavDebugColor.ObstacleSource), Has.Count.EqualTo(12), "the test box is outlined as obstacle-only");
			Assert.That(draw.Segments(NavDebugColor.WalkableSource), Has.Count.EqualTo(12), "the ground is outlined as walkable");
			Assert.That(clearance, Has.Count.EqualTo(4));
			Assert.That(clearance.SelectMany(s => new[] { s.A, s.B }).Select(p => (Math.Round(ToDouble(p.X), 3), Math.Round(ToDouble(p.Z), 3))).Distinct(),
				Is.EquivalentTo(new[] { (-2.5, -8.5), (2.5, -8.5), (2.5, -3.5), (-2.5, -3.5) }), "footprint grown by the baked agent radius");
			Assert.That(clearance.All(s => ToDouble(s.A.Y) == 0.5 && ToDouble(s.B.Y) == 0.5), "drawn on the ground the box stands on");
			Assert.That(walkableInsideClearance, Is.Zero, "no walkable triangle inside the obstacle's clearance");
			Assert.That(ring, Is.GreaterThanOrEqualTo(4), "the mesh boundary rings the obstacle just outside its clearance");
		});
	}

	[Test]
	public void OverlayShowsTheRouteTheAgentSelected() {
		CreateWorld<NavDebugDrawWorldA>(Level());
		var character = FindCharacter<NavDebugDrawWorldA>();
		character.Delete<ChasesNearestPlayer>();
		var destination = new F.FVector3(F.FP.Zero, F.FP.Half, F.FP.Zero);
		character.Ref<NavAgent>().SetDestination(destination);
		for (var i = 0; i < 20; i++) {
			Step<NavDebugDrawWorldA>();
		}

		var draw = new Recorder();
		var overlay = new Core<NavDebugDrawWorldA>.NavDebugDraw(Level());
		overlay.Draw(draw, NavDebugDrawFlags.Agents);
		var agent = character.Read<NavAgent>();
		var path = draw.Segments(NavDebugColor.Path);

		Assert.Multiple(() => {
			Assert.That(overlay.AgentCount, Is.EqualTo(1));
			Assert.That(draw.Agents, Has.Count.EqualTo(1));
			Assert.That(draw.Agents[0].Status, Is.EqualTo(NavAgentStatus.Moving));
			Assert.That(draw.Triangles.Where(t => t.Color == NavDebugColor.Corridor).Count(), Is.InRange(1, agent.CorridorLength - agent.CorridorIndex), "corridor ahead");
			Assert.That(draw.Segments(NavDebugColor.CurrentTriangle), Has.Count.EqualTo(3));
			Assert.That(draw.Points, Does.Contain((destination, NavDebugColor.Destination)));
			Assert.That(draw.Points, Does.Contain((agent.PathTarget, NavDebugColor.PathTarget)));
			Assert.That(path, Has.Count.GreaterThanOrEqualTo(2), "the route bends around the box");
			Assert.That(path[^1].B, Is.EqualTo(agent.PathTarget));
			Assert.That(path.Any(s => SegmentEntersBox(s.A, s.B)), Is.False, "the drawn route must not cross the box");
			Assert.That(draw.Points.Count(p => p.Color == NavDebugColor.Corner), Is.EqualTo(path.Count - 1));
			Assert.That(draw.Segments(NavDebugColor.Steering), Has.Count.EqualTo(1));
		});

		// Selecting a missing agent draws none; the count stays.
		var none = new Recorder();
		overlay.Draw(none, NavDebugDrawFlags.Agents, selectedAgent: 1);
		Assert.That(none.Agents, Is.Empty);
		Assert.That(overlay.AgentCount, Is.EqualTo(1));
	}

	[Test]
	public void FailedPlanDrawsALineToTheUnreachableDestination() {
		CreateWorld<NavDebugDrawWorldA>(Level());
		var character = FindCharacter<NavDebugDrawWorldA>();
		character.Delete<ChasesNearestPlayer>();
		// Far off the ground, beyond the destination snap distance.
		var unreachable = new F.FVector3(F.FP.FromRatio(100, 1), F.FP.Three / 2, F.FP.Zero);
		character.Ref<NavAgent>().SetDestination(unreachable);
		Step<NavDebugDrawWorldA>();

		var draw = new Recorder();
		new Core<NavDebugDrawWorldA>.NavDebugDraw(Level()).Draw(draw, NavDebugDrawFlags.Agents);

		Assert.Multiple(() => {
			Assert.That(draw.Agents.Single().Status, Is.EqualTo(NavAgentStatus.Failed));
			Assert.That(draw.Agents.Single().PathStatus, Is.EqualTo(NavPathStatus.EndOffMesh));
			Assert.That(draw.Segments(NavDebugColor.FailedPath).Single().B, Is.EqualTo(unreachable));
			Assert.That(draw.Segments(NavDebugColor.Path), Is.Empty);
		});
	}

	[Test]
	public void LevelWithoutNavigationDrawsOnlyItsColliders() {
		var level = Level().WithNavigation(null);
		CreateWorld<NavDebugDrawWorldA>(level);
		var draw = new Recorder();
		var overlay = new Core<NavDebugDrawWorldA>.NavDebugDraw(level);
		overlay.Draw(draw);

		Assert.Multiple(() => {
			Assert.That(overlay.Mesh, Is.Null);
			Assert.That(draw.Triangles, Is.Empty);
			Assert.That(draw.Agents, Is.Empty);
			Assert.That(draw.Segments(NavDebugColor.ObstacleSource), Has.Count.EqualTo(12));
			Assert.That(draw.Segments(NavDebugColor.ObstacleClearance), Is.Empty, "no bake, no clearance");
		});
	}

	[Test]
	public void DrawingEveryTickLeavesTheSimulationUnchanged() {
		CreateWorld<NavDebugUndrawnWorld>(Level());
		CreateWorld<NavDebugDrawnWorld>(Level());
		AddPlayer<NavDebugUndrawnWorld>();
		AddPlayer<NavDebugDrawnWorld>();
		var overlay = new Core<NavDebugDrawnWorld>.NavDebugDraw(Level());
		var areas = Level().Navigation!.Mesh.Areas.ToArray();
		var worldAreas = Core<NavDebugDrawnWorld>.Systems.GetResource<NavigationRes>().Mesh!.Areas.ToArray();
		var undrawn = SnapshotWriter<NavDebugUndrawnWorld>();
		var drawn = SnapshotWriter<NavDebugDrawnWorld>();
		var paths = 0;

		for (var tick = 0; tick < Ticks; tick++) {
			StepWithInput<NavDebugUndrawnWorld>(tick);
			StepWithInput<NavDebugDrawnWorld>(tick);
			var draw = new Recorder();
			overlay.Draw(draw, NavDebugDrawFlags.Default, selectedAgent: tick % 2 - 1);
			if (draw.Segments(NavDebugColor.Path).Count > 0) {
				paths++;
			}
			Assert.That(WorldHash<NavDebugDrawnWorld>(ref drawn), Is.EqualTo(WorldHash<NavDebugUndrawnWorld>(ref undrawn)), $"world hash after tick {tick}");
		}

		Assert.Multiple(() => {
			Assert.That(paths, Is.GreaterThan(Ticks / 2), "the overlay must have drawn the chase");
			Assert.That(Bytes(Core<NavDebugDrawnWorld>.Systems.GetResource<NavigationRes>().Mesh!.Areas.ToArray()), Is.EqualTo(Bytes(worldAreas)), "runtime areas");
			Assert.That(Bytes(Level().Navigation!.Mesh.Areas.ToArray()), Is.EqualTo(Bytes(areas)), "shipped areas");
		});
	}

	private static void AddPlayer<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.W.NewEntity(new Player { PlayerGuid = new Guid(1, 0, 0, new byte[8]), InputChannel = 0 });
	}

	/// <summary>The player walks right past the box, then back left, so the chase re-plans around it.</summary>
	private static void StepWithInput<TWorld>(int tick) where TWorld : struct, IWorldType, ISessionType {
		var input = (tick % 160) switch {
			< 60 => new PlayerInput { MoveX = F.FP.One },
			< 120 => new PlayerInput { MoveX = -F.FP.One, MoveY = -F.FP.Half },
			_ => default,
		};
		Core<TWorld>.S.SetApprovedInput(0, input);
		Step<TWorld>();
	}

	private static byte[] Bytes(NavTriangleArea[] areas) {
		return areas.SelectMany(a => NavTestSession.Bytes(a)).ToArray();
	}

	private static bool SegmentEntersBox(F.FVector3 a, F.FVector3 b) {
		for (var i = 0; i <= 100; i++) {
			var t = i / 100.0;
			var x = ToDouble(a.X) + (ToDouble(b.X) - ToDouble(a.X)) * t;
			var z = ToDouble(a.Z) + (ToDouble(b.Z) - ToDouble(a.Z)) * t;
			if (Inside(x, z, BoxMinX, BoxMaxX, BoxMinZ, BoxMaxZ)) {
				return true;
			}
		}
		return false;
	}

	private static bool Inside(double x, double z, double minX, double maxX, double minZ, double maxZ) {
		return x > minX && x < maxX && z > minZ && z < maxZ;
	}

	private static (double X, double Z) Midpoint((F.FVector3 A, F.FVector3 B, NavDebugColor Color) s) {
		return ((ToDouble(s.A.X) + ToDouble(s.B.X)) / 2, (ToDouble(s.A.Z) + ToDouble(s.B.Z)) / 2);
	}

	private static (double X, double Z) Centroid((F.FVector3 A, F.FVector3 B, F.FVector3 C, NavDebugColor Color) t) {
		return ((ToDouble(t.A.X) + ToDouble(t.B.X) + ToDouble(t.C.X)) / 3, (ToDouble(t.A.Z) + ToDouble(t.B.Z) + ToDouble(t.C.Z)) / 3);
	}

	private static double ToDouble(F.FP value) => F.FConversions.ToDouble(value);

	[Test]
	public void ZonesAreOutlinedByTheirState() {
		CreateWorld<NavDebugDrawWorldA>(Level());
		var overlay = new Core<NavDebugDrawWorldA>.NavDebugDraw(Level());
		var open = new Recorder();
		overlay.Draw(open, NavDebugDrawFlags.Triangles | NavDebugDrawFlags.Sources);

		foreach (var entity in World<NavDebugDrawWorldA>.Query<All<NavZoneState>>().Entities()) {
			entity.Ref<NavZoneState>().Blocked = true;
		}
		Step<NavDebugDrawWorldA>();
		var closed = new Recorder();
		overlay.Draw(closed, NavDebugDrawFlags.Triangles | NavDebugDrawFlags.Sources);

		var zoneTriangles = Level().Navigation!.Zones[0].Triangles.Length;
		Assert.Multiple(() => {
			Assert.That(open.Segments(NavDebugColor.OpenZone), Has.Count.EqualTo(12), "the gate volume is outlined as open");
			Assert.That(open.Segments(NavDebugColor.BlockedZone), Is.Empty);
			Assert.That(closed.Segments(NavDebugColor.BlockedZone), Has.Count.EqualTo(12), "and as blocked once closed");
			Assert.That(closed.Triangles.Count(t => t.Color == NavDebugColor.BlockedArea), Is.EqualTo(zoneTriangles), "its triangles are drawn blocked");
		});
	}

	private sealed class Recorder : INavDebugDraw {
		public List<(F.FVector3 A, F.FVector3 B, F.FVector3 C, NavDebugColor Color)> Triangles { get; } = [];
		public List<(F.FVector3 A, F.FVector3 B, NavDebugColor Color)> AllSegments { get; } = [];
		public List<(F.FVector3 Point, NavDebugColor Color)> Points { get; } = [];
		public List<NavAgent> Agents { get; } = [];

		public List<(F.FVector3 A, F.FVector3 B, NavDebugColor Color)> Segments(NavDebugColor color) {
			return AllSegments.Where(s => s.Color == color).ToList();
		}

		public void DrawTriangle(F.FVector3 a, F.FVector3 b, F.FVector3 c, NavDebugColor color) => Triangles.Add((a, b, c, color));
		public void DrawSegment(F.FVector3 start, F.FVector3 end, NavDebugColor color) => AllSegments.Add((start, end, color));
		public void DrawPoint(F.FVector3 point, NavDebugColor color) => Points.Add((point, color));
		public void DrawAgent(int index, F.FVector3 feet, in NavAgent agent) => Agents.Add(agent);
	}
}
