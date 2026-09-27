using System.Runtime.InteropServices;
using FFS.Libraries.StaticEcs;
using Fixed;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct NavAgentTestWorldA : IWorldType, ISessionType;

public struct NavAgentTestWorldB : IWorldType, ISessionType;

/// <summary>
/// Phase 5: a <see cref="NavCharacter"/> on the committed sample level. Its ground top is at y=0.5
/// and the obstacle-only test box covers x in [-2, 2], z in [-8, -4] up to y=1.5. The character
/// spawns behind the box at (0, -11), as seen from the player spawn at (0, 0).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class NavAgentTests {
	private const int MaxTicks = 600;
	private const double CapsuleRadius = 0.5;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<NavAgentTestWorldA>();
		DestroyIfCreated<NavAgentTestWorldB>();
	}

	[Test]
	public void AgentWalksAroundTheSampleObstacle() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		character.Ref<NavAgent>().SetDestination(Point(0, 0));

		var run = Run<NavAgentTestWorldA>(character, static agent => agent.Status == NavAgentStatus.Arrived);

		var agent = character.Read<NavAgent>();
		var position = character.Read<Transform>().Position;
		Assert.Multiple(() => {
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Arrived), $"stuck at {Describe(position)} after {run.Ticks} ticks ({agent.PathStatus})");
			Assert.That(DistanceXZ(position, Point(0, 0)), Is.LessThanOrEqualTo(ToDouble(agent.ArrivalRadius) + 0.01));
			Assert.That(run.MaxAbsX, Is.GreaterThan(2.0), "the route must leave the straight line through the box");
			Assert.That(run.MinBoxClearance, Is.GreaterThan(CapsuleRadius - 0.05), "the capsule must not enter the test box");
			Assert.That(ToDouble(position.Y), Is.EqualTo(1.5).Within(0.05), "the character stays grounded on the floor");
		});
	}

	[Test]
	public void CollidersMissingFromTheNavmeshStillBlockMovement() {
		// A wall across the straight route that the shipped navmesh does not know about.
		var level = Level();
		var wall = new StaticBox(
			"Test/UnbakedWall",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.One, F.FP.FromRatio(-15, 1)), Fixed32.FQuaternion.Identity),
			new Fixed32.FVector3(Fixed32.FP.FromRatio(3, 1), Fixed32.FP.Half, Fixed32.FP.FromRatio(1, 4)));
		CreateWorld<NavAgentTestWorldA>(new LevelData([], level.StaticBoxes.Append(wall), level.Navigation));
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		character.Ref<NavAgent>().SetDestination(Point(0, -20));

		var minClearance = double.MaxValue;
		for (var i = 0; i < 300; i++) {
			Step<NavAgentTestWorldA>();
			minClearance = Math.Min(minClearance, Clearance(character.Read<Transform>().Position, -3, 3, -15.25, -14.75));
		}

		// The mover slides along the wall and around its end, so only the overlap is checked.
		Assert.That(minClearance, Is.GreaterThan(CapsuleRadius - 0.05), "the capsule must not enter the unbaked wall");
	}

	[Test]
	public void ChasesThePlayerAroundTheObstacle() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = FindCharacter<NavAgentTestWorldA>();
		var player = Core<NavAgentTestWorldA>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });

		var run = Run<NavAgentTestWorldA>(character, static agent => agent.Status == NavAgentStatus.Arrived);

		var distance = DistanceXZ(character.Read<Transform>().Position, player.Read<Transform>().Position);
		Assert.Multiple(() => {
			Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Arrived));
			Assert.That(distance, Is.LessThanOrEqualTo(ToDouble(character.Read<NavAgent>().ArrivalRadius) + 0.01));
			Assert.That(run.MinBoxClearance, Is.GreaterThan(CapsuleRadius - 0.05));
		});
	}

	[Test]
	public void ApproachesAPlayerStandingOnTopOfTheBox() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = FindCharacter<NavAgentTestWorldA>();
		var player = Core<NavAgentTestWorldA>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });
		// The middle of the box top, 2.5 from the nearest walkable ground.
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(5, 2), F.FP.FromRatio(-6, 1));

		var run = Run<NavAgentTestWorldA>(character, static agent => agent.Status is NavAgentStatus.Arrived or NavAgentStatus.Failed);

		var agent = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Arrived), $"{agent.PathStatus}");
			Assert.That(ToDouble(player.Read<Transform>().Position.Y), Is.EqualTo(2.5).Within(0.05), "the player must still be on the box");
			Assert.That(run.MinBoxClearance, Is.GreaterThan(CapsuleRadius - 0.05));
			Assert.That(DistanceXZ(character.Read<Transform>().Position, player.Read<Transform>().Position), Is.LessThan(4.0));
		});
	}

	[Test]
	public void FollowsAgainAfterThePlayerJumpsOffTheBox() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = FindCharacter<NavAgentTestWorldA>();
		var player = Core<NavAgentTestWorldA>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });
		// Near the box's front edge: the character waits on the navmesh boundary at z=-3.25, which
		// is also a lookup grid line, and used to get stuck there once it drifted a hair past it.
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(5, 2), F.FP.FromRatio(-9, 2));

		for (var tick = 0; tick < 500; tick++) {
			var input = new PlayerInput();
			if (tick is >= 200 and < 290) {
				input.MoveX = F.FP.One;
				input.Jump = tick == 200;
			}
			Core<NavAgentTestWorldA>.S.SetPredictionInput(channel: 0, input);
			Step<NavAgentTestWorldA>();
		}

		var agent = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(ToDouble(player.Read<Transform>().Position.Y), Is.EqualTo(1.5).Within(0.05), "the player must be back on the ground");
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Arrived), $"{agent.PathStatus}");
			Assert.That(DistanceXZ(character.Read<Transform>().Position, player.Read<Transform>().Position), Is.LessThanOrEqualTo(ToDouble(agent.ArrivalRadius) + 1.0));
		});
	}

	[Test]
	public void PlansFromJustOutsideTheMeshEdgeOnAGridLine() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		// 0.000015 below the z=-3.25 mesh edge: within the point-in-triangle tolerance of the
		// triangle above it, but in the grid cell below, which does not list that triangle.
		character.Ref<Transform>().Position = new F.FVector3(F.FP.FromRaw(-3114598400), F.FP.Three / 2, F.FP.FromRaw(-6979354624));
		character.Ref<NavAgent>().SetDestination(Point(10, 0));

		Step<NavAgentTestWorldA>();

		var agent = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(agent.CurrentTriangle, Is.GreaterThanOrEqualTo(0));
			Assert.That(agent.PathStatus, Is.EqualTo(NavPathStatus.Found));
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Moving));
		});
	}

	[Test]
	public void ChaserStandsStillWithoutPlayers() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = FindCharacter<NavAgentTestWorldA>();
		var start = character.Read<Transform>().Position;

		for (var i = 0; i < 30; i++) {
			Step<NavAgentTestWorldA>();
		}

		Assert.Multiple(() => {
			Assert.That(character.Read<NavAgent>().HasDestination, Is.False);
			Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Idle));
			Assert.That(DistanceXZ(character.Read<Transform>().Position, start), Is.LessThan(1e-6));
		});
	}

	[Test]
	public void ChangingTheDestinationReplansOnTheNextTick() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		character.Ref<NavAgent>().SetDestination(Point(10, -11));
		for (var i = 0; i < 10; i++) {
			Step<NavAgentTestWorldA>();
		}
		Assert.That(character.Read<CharacterMoveIntent>().Velocity.X, Is.GreaterThan(F.FP.Zero));

		var other = Point(-10, -11);
		character.Ref<NavAgent>().SetDestination(other);
		Step<NavAgentTestWorldA>();

		var agent = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(agent.PlannedDestination.Equals(other), Is.True);
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Moving));
			Assert.That(character.Read<CharacterMoveIntent>().Velocity.X, Is.LessThan(F.FP.Zero));
		});
	}

	[Test]
	public void ReplansOnTheTickInterval() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		character.Ref<NavAgent>().SetDestination(Point(0, 20));
		Step<NavAgentTestWorldA>();
		var interval = character.Read<NavAgent>().RepathIntervalTicks;
		var first = character.Read<NavAgent>().NextRepathTick;

		while (Core<NavAgentTestWorldA>.S.CurrentTick < first) {
			Step<NavAgentTestWorldA>();
		}
		Step<NavAgentTestWorldA>();

		Assert.That(character.Read<NavAgent>().NextRepathTick, Is.EqualTo(first + interval));
	}

	[TestCase(100, 0, TestName = "DestinationOffTheGroundFails")]
	public void UnreachableDestinationFailsInPlace(int x, int z) {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		var start = character.Read<Transform>().Position;
		character.Ref<NavAgent>().SetDestination(new F.FVector3(F.FP.FromRatio(x, 1), F.FP.Three / 2, F.FP.FromRatio(z, 1)));

		for (var i = 0; i < 30; i++) {
			Step<NavAgentTestWorldA>();
		}

		var agent = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(agent.Status, Is.EqualTo(NavAgentStatus.Failed));
			Assert.That(agent.PathStatus, Is.EqualTo(NavPathStatus.EndOffMesh));
			Assert.That(agent.CorridorLength, Is.Zero);
			Assert.That(DistanceXZ(character.Read<Transform>().Position, start), Is.LessThan(1e-6));
		});
	}

	[Test]
	public void TwoWorldsInOneProcessProduceIdenticalAgents() {
		CreateWorld<NavAgentTestWorldA>(Level());
		CreateWorld<NavAgentTestWorldB>(Level());
		var a = FindCharacter<NavAgentTestWorldA>();
		var b = FindCharacter<NavAgentTestWorldB>();
		Core<NavAgentTestWorldA>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });
		Core<NavAgentTestWorldB>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });

		var planned = false;
		for (var i = 0; i < 240; i++) {
			// Interleaved, so any navigation scratch shared between the worlds would show.
			Step<NavAgentTestWorldA>();
			Step<NavAgentTestWorldB>();
			Assert.That(Bytes(b.Read<NavAgent>()), Is.EqualTo(Bytes(a.Read<NavAgent>())), $"NavAgent diverged on tick {i}");
			Assert.That(Bytes(b.Read<Transform>()), Is.EqualTo(Bytes(a.Read<Transform>())), $"Transform diverged on tick {i}");
			planned |= a.Read<NavAgent>().CorridorLength > 1;
		}
		Assert.That(planned, Is.True, "the scenario must exercise a multi-triangle corridor");
	}

	[Test]
	public void PathStateRoundTripsThroughAWorldSnapshot() {
		CreateWorld<NavAgentTestWorldA>(Level());
		var character = TakeOverCharacter<NavAgentTestWorldA>();
		character.Ref<NavAgent>().SetDestination(Point(0, 0));
		for (var i = 0; i < 20; i++) {
			Step<NavAgentTestWorldA>();
		}
		var gid = character.GID;
		var expected = Bytes(character.Read<NavAgent>());
		Assert.That(character.Read<NavAgent>().CorridorLength, Is.GreaterThan(1));

		var snapshot = Core<NavAgentTestWorldA>.W.Serializer.CreateWorldSnapshot();
		character.Ref<NavAgent>().ClearDestination();
		character.Ref<NavAgent>().Corridor[0] = -7;
		Core<NavAgentTestWorldA>.W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);

		Assert.That(gid.TryUnpack<NavAgentTestWorldA>(out var restored), Is.True);
		Assert.That(Bytes(restored.Read<NavAgent>()), Is.EqualTo(expected));
	}

	[Test]
	public void LevelWithoutNavigationSpawnsNoCharacter() {
		CreateWorld<NavAgentTestWorldA>(Level().WithNavigation(null));

		Assert.That(Core<NavAgentTestWorldA>.W.Query<All<NavAgent>>().EntitiesCount(), Is.Zero);
	}

	private readonly record struct RunResult(int Ticks, double MaxAbsX, double MinBoxClearance);

	/// <summary>Steps until <paramref name="done"/> or <see cref="MaxTicks"/>, tracking the route's shape.</summary>
	private static RunResult Run<TWorld>(World<TWorld>.Entity character, Func<NavAgent, bool> done) where TWorld : struct, IWorldType, ISessionType {
		var maxAbsX = 0.0;
		var minClearance = double.MaxValue;
		var ticks = 0;
		while (ticks < MaxTicks && !done(character.Read<NavAgent>())) {
			Step<TWorld>();
			ticks++;
			var position = character.Read<Transform>().Position;
			maxAbsX = Math.Max(maxAbsX, Math.Abs(ToDouble(position.X)));
			minClearance = Math.Min(minClearance, Clearance(position, -2, 2, -8, -4));
		}
		return new RunResult(ticks, maxAbsX, minClearance);
	}

	/// <summary>XZ distance from the capsule axis to an axis-aligned box footprint.</summary>
	private static double Clearance(F.FVector3 position, double minX, double maxX, double minZ, double maxZ) {
		var x = ToDouble(position.X);
		var z = ToDouble(position.Z);
		var dx = Math.Max(Math.Max(minX - x, x - maxX), 0);
		var dz = Math.Max(Math.Max(minZ - z, z - maxZ), 0);
		return Math.Sqrt(dx * dx + dz * dz);
	}

	/// <summary>The spawned character, detached from the chase behavior so the test sets its destination.</summary>
	private static World<TWorld>.Entity TakeOverCharacter<TWorld>() where TWorld : struct, IWorldType {
		var character = FindCharacter<TWorld>();
		character.Delete<ChasesNearestPlayer>();
		return character;
	}

	/// <summary>A destination on the ground surface at world (x, z).</summary>
	private static F.FVector3 Point(int x, int z) => new(F.FP.FromRatio(x, 1), F.FP.Half, F.FP.FromRatio(z, 1));

	private static double ToDouble(F.FP value) => F.FConversions.ToDouble(value);

	private static double DistanceXZ(F.FVector3 a, F.FVector3 b) {
		var dx = ToDouble(a.X) - ToDouble(b.X);
		var dz = ToDouble(a.Z) - ToDouble(b.Z);
		return Math.Sqrt(dx * dx + dz * dz);
	}

	private static string Describe(F.FVector3 v) => $"({ToDouble(v.X):F2}, {ToDouble(v.Y):F2}, {ToDouble(v.Z):F2})";
}
