using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct NavRollbackWorldA : IWorldType, ISessionType;

// Worlds whose snapshot hashes are compared are each created once per process: StaticEcs writes a
// recreated world's resources in a different order, so the bytes of equal states would differ.
public struct NavUninterruptedWorld : IWorldType, ISessionType;

public struct NavRolledBackWorld : IWorldType, ISessionType;

public struct NavSyncSourceWorld : IWorldType, ISessionType;

public struct NavSyncTargetWorld : IWorldType, ISessionType;

/// <summary>
/// Phase 6: navigation through prediction, rollback, full synchronization, and re-simulation. Two
/// players on the sample level; the remote one walks toward the chasing character and away again,
/// so which player it chases, and therefore its destination and corridor, depends on that input.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class NavRollbackTests {
	private const ushort LocalChannel = 0;
	private const ushort RemoteChannel = 1;
	private const int Ticks = 240;

	/// <summary>How late the remote player's input reaches the rollback run, in ticks.</summary>
	private const int InputDelay = 12;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<NavRollbackWorldA>();
		DestroyIfCreated<NavUninterruptedWorld>();
		DestroyIfCreated<NavRolledBackWorld>();
		DestroyIfCreated<NavSyncSourceWorld>();
		DestroyIfCreated<NavSyncTargetWorld>();
	}

	[Test]
	public void EveryNavAgentFieldSurvivesSnapshotRestore() {
		CreateWorld<NavRollbackWorldA>(Level());
		var character = FindCharacter<NavRollbackWorldA>();
		ref var agent = ref character.Ref<NavAgent>();
		// Distinct non-zero values everywhere, including every corridor slot.
		agent = NavAgent.Create(F.FP.FromRatio(13, 7), F.FP.FromRatio(5, 3), F.FP.FromRatio(11, 4), 17, 0x5A5A, F.FP.FromRatio(13, 5));
		agent.SetDestination(new F.FVector3(F.FP.FromRatio(-7, 3), F.FP.FromRatio(1, 9), F.FP.FromRatio(22, 7)));
		agent.Status = NavAgentStatus.Moving;
		agent.PathStatus = NavPathStatus.FoundTruncated;
		agent.PlannedDestination = new F.FVector3(F.FP.FromRatio(3, 11), F.FP.FromRatio(-2, 5), F.FP.FromRatio(9, 2));
		agent.PathTarget = new F.FVector3(F.FP.FromRatio(-1, 13), F.FP.FromRatio(4, 7), F.FP.FromRatio(-8, 3));
		agent.CurrentTriangle = 41;
		agent.NextRepathTick = 1234;
		agent.CorridorIndex = 5;
		agent.CorridorLength = NavAgent.CorridorCapacity;
		for (var i = 0; i < NavAgent.CorridorCapacity; i++) {
			agent.Corridor[i] = 1000 + i * 7;
		}
		var expected = Bytes(agent);
		var gid = character.GID;

		var snapshot = World<NavRollbackWorldA>.Serializer.CreateWorldSnapshot();
		character.Ref<NavAgent>() = default;
		World<NavRollbackWorldA>.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);

		Assert.That(gid.TryUnpack<NavRollbackWorldA>(out var restored), Is.True);
		Assert.That(Bytes(restored.Read<NavAgent>()), Is.EqualTo(expected));
	}

	[Test]
	public void RollbackAcrossDestinationChangesMatchesTheUninterruptedRun() {
		var reference = Record<NavUninterruptedWorld>(SimulationType.ForwardOnly, inputDelay: 0);
		var rolledBack = Record<NavRolledBackWorld>(SimulationType.AutomaticRollbacks, InputDelay);

		var mispredictedPlans = 0;
		foreach (var (tick, planned) in rolledBack.PlannedDestination) {
			if (!rolledBack.FirstPlannedDestination[tick].Equals(planned)) {
				mispredictedPlans++;
			}
		}
		var divergedAt = -1;
		for (var tick = reference.StartTick; tick < reference.StartTick + Ticks; tick++) {
			if (!rolledBack.Hashes.TryGetValue(tick, out var hash) || hash != reference.Hashes[tick]) {
				divergedAt = tick;
				break;
			}
		}

		Assert.Multiple(() => {
			Assert.That(reference.Resimulated, Is.Zero, "the reference run must not roll back");
			Assert.That(rolledBack.Resimulated, Is.GreaterThan(Ticks), "late input must force re-simulation");
			Assert.That(mispredictedPlans, Is.GreaterThan(0), "a rollback must replace a destination the prediction planned");
			Assert.That(divergedAt, Is.EqualTo(-1), "first tick whose world hash differs from the uninterrupted run");
		});
	}

	[Test]
	public void FullSyncIntoAnotherWorldTypeAdvancesIdentically() {
		CreateWorld<NavSyncSourceWorld>(Level());
		AddPlayers<NavSyncSourceWorld>();
		for (var i = 0; i < 90; i++) {
			StepWithInput<NavSyncSourceWorld>();
		}
		Assert.That(FindCharacter<NavSyncSourceWorld>().Read<NavAgent>().CorridorLength, Is.GreaterThan(1), "sync mid-path");

		CreateWorld<NavSyncTargetWorld>(Level());
		var sourceWriter = SnapshotWriter<NavSyncSourceWorld>();
		new Core<NavSyncSourceWorld>.GameWorldFullSyncHandler().WriteFullSync(ref sourceWriter);
		// What Client.HandleFullSync does: restart the session at the server tick, then load the world.
		Core<NavSyncTargetWorld>.S.HardReset(Core<NavSyncSourceWorld>.S.CurrentTick);
		var reader = sourceWriter.AsReader();
		new Core<NavSyncTargetWorld>.GameWorldFullSyncHandler().ReadFullSync(ref reader);

		var writerA = SnapshotWriter<NavSyncSourceWorld>();
		var writerB = SnapshotWriter<NavSyncTargetWorld>();
		Assert.That(WorldHash<NavSyncTargetWorld>(ref writerB), Is.EqualTo(WorldHash<NavSyncSourceWorld>(ref writerA)), "synchronized state");
		for (var i = 0; i < Ticks; i++) {
			StepWithInput<NavSyncSourceWorld>();
			StepWithInput<NavSyncTargetWorld>();
			Assert.That(WorldHash<NavSyncTargetWorld>(ref writerB), Is.EqualTo(WorldHash<NavSyncSourceWorld>(ref writerA)),
				$"world hash after tick {Core<NavSyncSourceWorld>.S.CurrentTick}");
		}
	}

	[Test]
	public void SnapshotCostPerAgentIsMeasured() {
		CreateWorld<NavRollbackWorldA>(Level());
		var writer = SnapshotWriter<NavRollbackWorldA>();
		var baseline = SnapshotLength<NavRollbackWorldA>(ref writer);
		const int added = 64;
		for (var i = 0; i < added; i++) {
			var character = Core<NavRollbackWorldA>.W.NewEntity<NavCharacter>();
			character.Set(new Transform { Position = new F.FVector3(F.FP.FromRatio(i % 8 * 2 - 8, 1), F.FP.Three / 2, F.FP.FromRatio(i / 8 * 2 + 4, 1)), Rotation = F.FQuaternion.Identity });
			character.Set(NavAgent.Create(F.FP.FromRatio(4, 1), F.FP.One, F.FP.Two, 30));
		}
		var perAgent = (SnapshotLength<NavRollbackWorldA>(ref writer) - baseline) / added;
		var capacity = (Core<NavRollbackWorldA>.GameWorldRollback.WorldSnapshotLength - baseline) / perAgent;
		TestContext.Out.WriteLine($"snapshot: {baseline} bytes for the sample world, {perAgent} bytes per navigation character " +
			$"(NavAgent {System.Runtime.CompilerServices.Unsafe.SizeOf<NavAgent>()}), room for {capacity} characters in the {Core<NavRollbackWorldA>.GameWorldRollback.WorldSnapshotLength}-byte frame");

		Assert.Multiple(() => {
			Assert.That(System.Runtime.CompilerServices.Unsafe.SizeOf<NavAgent>(), Is.EqualTo(400), "update the documented snapshot cost");
			Assert.That(perAgent, Is.LessThan(1024));
			Assert.That(capacity, Is.GreaterThanOrEqualTo(256), "rollback frames must hold at least 256 navigation characters");
		});
	}

	[Test]
	public void LongestSampleCorridorFitsTheAgentBuffer() {
		var mesh = Level().Navigation!.Mesh.CreateMesh();
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var corridor = new int[mesh.TriangleCount];
		var longest = 0;
		for (var from = 0; from < mesh.TriangleCount; from++) {
			for (var to = 0; to < mesh.TriangleCount; to++) {
				var start = Centroid(mesh, from);
				var end = Centroid(mesh, to);
				var status = pathfinder.FindPath(start, from, end, to, ~0, corridor, out var length);
				Assert.That(status, Is.EqualTo(NavPathStatus.Found), $"{from} -> {to}");
				longest = Math.Max(longest, length);
			}
		}
		TestContext.Out.WriteLine($"longest corridor on the sample mesh: {longest} of {mesh.TriangleCount} triangles; agent buffer {NavAgent.CorridorCapacity}");

		Assert.That(longest, Is.LessThanOrEqualTo(NavAgent.CorridorCapacity));
	}

	[Test]
	public void SteadyStateNavigationAllocatesNothing() {
		// The session loop allocates on its own, so compare against the same run without a navmesh,
		// which spawns no character. The difference is what the character's planning and movement cost.
		var withNavigation = SteadyStateAllocation(Level(), out var plans);
		var withoutNavigation = SteadyStateAllocation(Level().WithNavigation(null), out _);
		TestContext.Out.WriteLine($"allocated over 480 ticks: {withNavigation} bytes with navigation ({plans} plans), {withoutNavigation} without");

		Assert.Multiple(() => {
			Assert.That(plans, Is.EqualTo(480), "the character must plan every tick");
			Assert.That(withNavigation - withoutNavigation, Is.Zero);
		});
	}

	/// <summary>Bytes allocated over 480 ticks after a 480-tick warm-up, with the character planning every tick.</summary>
	private static long SteadyStateAllocation(LevelData level, out int plans) {
		CreateWorld<NavRollbackWorldA>(level);
		try {
			AddPlayers<NavRollbackWorldA>();
			var hasCharacter = level.Navigation is not null;
			if (hasCharacter) {
				FindCharacter<NavRollbackWorldA>().Ref<NavAgent>().RepathIntervalTicks = 1;
			}
			for (var i = 0; i < 480; i++) {
				StepWithInput<NavRollbackWorldA>();
			}

			plans = 0;
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 480; i++) {
				StepWithInput<NavRollbackWorldA>();
				if (hasCharacter && FindCharacter<NavRollbackWorldA>().Read<NavAgent>().NextRepathTick == Core<NavRollbackWorldA>.S.CurrentTick) {
					plans++;
				}
			}
			return GC.GetAllocatedBytesForCurrentThread() - before;
		} finally {
			DestroyIfCreated<NavRollbackWorldA>();
		}
	}

	/// <summary>Plays the scripted inputs, delivering the remote player's <paramref name="inputDelay"/> ticks late.</summary>
	private static TickRecorder<TWorld> Record<TWorld>(SimulationType simulationType, int inputDelay) where TWorld : struct, IWorldType, ISessionType {
		CreateWorld<TWorld>(Level(), simulationType);
		AddPlayers<TWorld>();
		// The client saves a first frame right after its full sync.
		Core<TWorld>.S.SaveFrame();
		var start = Core<TWorld>.S.CurrentTick;
		var recorder = new TickRecorder<TWorld>(start);
		Core<TWorld>.RollbackObserver = recorder;

		var end = start + Ticks;
		for (var tick = start; tick < end + inputDelay; tick++) {
			var remoteTick = tick - inputDelay;
			if (remoteTick >= start && remoteTick < end) {
				Core<TWorld>.S.SetApprovedInputAt(remoteTick, RemoteChannel, ScriptedInput(RemoteChannel, remoteTick - start));
			}
			if (tick < end) {
				Core<TWorld>.S.SetApprovedInput(LocalChannel, ScriptedInput(LocalChannel, tick - start));
				Step<TWorld>();
			} else {
				Core<TWorld>.S.FastForwardToTick(end);
			}
		}
		Assert.That(Core<TWorld>.S.CurrentTick, Is.EqualTo(end));
		return recorder;
	}

	/// <summary>Both players' scripted input for the current tick, delivered on time.</summary>
	private static void StepWithInput<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		var tick = Core<TWorld>.S.CurrentTick;
		Core<TWorld>.S.SetApprovedInput(LocalChannel, ScriptedInput(LocalChannel, tick));
		Core<TWorld>.S.SetApprovedInput(RemoteChannel, ScriptedInput(RemoteChannel, tick));
		Step<TWorld>();
	}

	private static void AddPlayers<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.W.NewEntity(new Player { PlayerGuid = new Guid(1, 0, 0, new byte[8]), InputChannel = LocalChannel });
		Core<TWorld>.W.NewEntity(new Player { PlayerGuid = new Guid(2, 0, 0, new byte[8]), InputChannel = RemoteChannel });
	}

	/// <summary>
	/// The local player (spawned at x=0, z=0) walks left and stops. The remote one (x=3) walks toward
	/// the character (spawned at z=-11) and becomes the nearer target, walks right past the box, and
	/// returns. Repeats every 240 ticks.
	/// </summary>
	private static PlayerInput ScriptedInput(ushort channel, int tick) {
		tick %= 240;
		if (channel == LocalChannel) {
			return tick is >= 30 and < 90 ? new PlayerInput { MoveX = -F.FP.One } : default;
		}
		return tick switch {
			>= 20 and < 60 => new PlayerInput { MoveY = -F.FP.One },
			>= 60 and < 110 => new PlayerInput { MoveX = F.FP.One },
			>= 140 and < 200 => new PlayerInput { MoveX = -F.FP.One, MoveY = F.FP.One },
			_ => default,
		};
	}

	/// <summary>Records the world hash and the character's planned destination after every simulated tick.</summary>
	private sealed class TickRecorder<TWorld> : IRollbackObserver where TWorld : struct, IWorldType, ISessionType {
		private BinaryPackWriter _writer = SnapshotWriter<TWorld>();

		public TickRecorder(int startTick) {
			StartTick = startTick;
		}

		public int StartTick { get; }
		public Dictionary<int, ulong> Hashes { get; } = new();
		public Dictionary<int, F.FVector3> FirstPlannedDestination { get; } = new();
		public Dictionary<int, F.FVector3> PlannedDestination { get; } = new();

		/// <summary>Ticks simulated more than once.</summary>
		public int Resimulated { get; private set; }

		public void OnTickSimulated(int tick) {
			if (Hashes.ContainsKey(tick)) {
				Resimulated++;
			}
			Hashes[tick] = WorldHash<TWorld>(ref _writer);
			var planned = FindCharacter<TWorld>().Read<NavAgent>().PlannedDestination;
			FirstPlannedDestination.TryAdd(tick, planned);
			PlannedDestination[tick] = planned;
		}

		public void OnFullSync() { }
	}

	private static long SnapshotLength<TWorld>(ref BinaryPackWriter writer) where TWorld : struct, IWorldType, ISessionType {
		writer.Position = 0;
		World<TWorld>.Serializer.CreateWorldSnapshot(ref writer);
		return writer.Position;
	}

	private static F.FVector3 Centroid(NavMesh mesh, int triangle) {
		ref readonly var t = ref mesh.Triangles[triangle];
		return new F.FVector3(t.CenterXZ.X, t.CenterY, t.CenterXZ.Y);
	}
}
