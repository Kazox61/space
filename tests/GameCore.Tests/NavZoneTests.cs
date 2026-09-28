using System.Runtime.InteropServices;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct NavZoneTestWorld : IWorldType, ISessionType;

// Hash-compared worlds are each created once per process; see NavRollbackTests.
public struct NavZoneUninterruptedWorld : IWorldType, ISessionType;

public struct NavZoneRolledBackWorld : IWorldType, ISessionType;

public struct NavZoneSyncSourceWorld : IWorldType, ISessionType;

public struct NavZoneSyncTargetWorld : IWorldType, ISessionType;

/// <summary>Closes every navigation zone for ticks 40 to 139 of every 200, from inside the simulation.</summary>
public struct ZoneToggleSystem<TWorld> : ISystem where TWorld : struct, IWorldType, ISessionType {
	public static bool IsClosed(int tick) => tick % 200 is >= 40 and < 140;

	public void Update() {
		var closed = IsClosed(Core<TWorld>.S.CurrentTick);
		foreach (var entity in World<TWorld>.Query<All<NavZoneState>>().Entities()) {
			entity.Ref<NavZoneState>().Blocked = closed;
		}
	}
}

/// <summary>
/// Phase 8: switchable zones on the committed sample level. Its <c>gate</c> zone covers x in [2, 6],
/// z in [-9, -3], beside the obstacle-only test box (x in [-2, 2], z in [-8, -4]), so a route from
/// the character spawn at (0, -11) past the box's right side runs through it.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class NavZoneTests {
	private const ushort LocalChannel = 0;
	private const ushort RemoteChannel = 1;
	private const int Ticks = 240;
	private const int InputDelay = 12;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<NavZoneTestWorld>();
		DestroyIfCreated<NavZoneUninterruptedWorld>();
		DestroyIfCreated<NavZoneRolledBackWorld>();
		DestroyIfCreated<NavZoneSyncSourceWorld>();
		DestroyIfCreated<NavZoneSyncTargetWorld>();
	}

	[Test]
	public void EachZoneGetsOneOpenStateEntity() {
		CreateWorld<NavZoneTestWorld>(Level());
		var navigation = Navigation<NavZoneTestWorld>();

		var states = new List<NavZoneState>();
		foreach (var entity in World<NavZoneTestWorld>.Query<All<NavZoneState>>().Entities()) {
			states.Add(entity.Read<NavZoneState>());
		}

		Assert.Multiple(() => {
			Assert.That(navigation.Zones.Select(static zone => zone.Id), Is.EqualTo(new[] { "gate" }));
			Assert.That(navigation.FindZone("gate"), Is.Zero);
			Assert.That(navigation.FindZone("missing"), Is.EqualTo(-1));
			Assert.That(states, Is.EqualTo(new[] { NavZoneState.Open(0) }));
			Assert.That(AreaBytes(navigation.Mesh!), Is.EqualTo(BakedAreaBytes()));
		});
	}

	[Test]
	public void ZoneStateIsWrittenIntoExactlyItsTriangles() {
		CreateWorld<NavZoneTestWorld>(Level());
		var navigation = Navigation<NavZoneTestWorld>();
		var zone = GateTriangles();
		var baked = Level().Navigation!.Mesh.Areas.ToArray();
		var openSignature = navigation.ZoneSignature;

		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().Blocked = true;
		Step<NavZoneTestWorld>();
		var closedSignature = navigation.ZoneSignature;
		var areas = navigation.Mesh!.Areas.ToArray();
		Assert.Multiple(() => {
			for (var t = 0; t < areas.Length; t++) {
				Assert.That(areas[t].IsBlocked, Is.EqualTo(zone.Contains(t)), $"triangle {t} blocked");
				Assert.That(areas[t].CostMultiplier, Is.EqualTo(baked[t].CostMultiplier), $"triangle {t} cost");
			}
			Assert.That(closedSignature, Is.Not.EqualTo(openSignature));
			Assert.That(navigation.IsZoneBlocked(0), Is.True);
		});

		ref var state = ref ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>();
		state.Blocked = false;
		state.CostMultiplier = F.FP.FromRatio(3, 1);
		Step<NavZoneTestWorld>();
		areas = navigation.Mesh!.Areas.ToArray();
		Assert.Multiple(() => {
			for (var t = 0; t < areas.Length; t++) {
				Assert.That(areas[t].IsBlocked, Is.False, $"triangle {t} blocked");
				Assert.That(areas[t].CostMultiplier, Is.EqualTo(zone.Contains(t) ? baked[t].CostMultiplier * 3 : baked[t].CostMultiplier), $"triangle {t} cost");
			}
			Assert.That(navigation.ZoneSignature, Is.Not.EqualTo(openSignature).And.Not.EqualTo(closedSignature));
		});

		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>() = NavZoneState.Open(0);
		Step<NavZoneTestWorld>();
		Assert.Multiple(() => {
			Assert.That(AreaBytes(navigation.Mesh!), Is.EqualTo(BakedAreaBytes()), "an open zone restores the baked areas");
			Assert.That(navigation.ZoneSignature, Is.EqualTo(openSignature));
		});
	}

	[Test]
	public void ZoneStatesCombineWithoutDependingOnOrder() {
		CreateWorld<NavZoneTestWorld>(Level());
		var navigation = Navigation<NavZoneTestWorld>();
		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().CostMultiplier = F.FP.Two;
		Core<NavZoneTestWorld>.W.NewEntity<Default>().Set(new NavZoneState { Zone = 0, Blocked = true, CostMultiplier = F.FP.Zero });
		Core<NavZoneTestWorld>.W.NewEntity<Default>().Set(new NavZoneState { Zone = 7, Blocked = true, CostMultiplier = F.FP.FromRatio(3, 1) });

		Step<NavZoneTestWorld>();

		Assert.Multiple(() => {
			Assert.That(navigation.IsZoneBlocked(0), Is.True, "any blocked state blocks");
			Assert.That(navigation.ZoneCostMultiplier(0), Is.EqualTo(F.FP.Two), "the highest cost wins; zero counts as one");
		});

		navigation.ResetZones();
		navigation.AddZoneState(new NavZoneState { Zone = 0, CostMultiplier = -F.FP.One });
		Assert.That(navigation.ZoneCostMultiplier(0), Is.EqualTo(F.FP.One), "negative costs count as one");
	}

	[Test]
	public void ClosingAZoneReplansAroundItAndReopeningRestoresTheShortPath() {
		CreateWorld<NavZoneTestWorld>(Level());
		var character = TakeOverCharacter<NavZoneTestWorld>();
		var zone = GateTriangles();
		character.Ref<NavAgent>().SetDestination(Point(4, 0));

		Step<NavZoneTestWorld>();
		var open = character.Read<NavAgent>();
		Assert.That(open.Status, Is.EqualTo(NavAgentStatus.Moving));
		Assert.That(Corridor(open).Any(zone.Contains), Is.True, "the short route passes the box on the gate side");

		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().Blocked = true;
		Step<NavZoneTestWorld>();
		var closed = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(closed.Status, Is.EqualTo(NavAgentStatus.Moving));
			Assert.That(closed.PlannedZoneSignature, Is.EqualTo(Navigation<NavZoneTestWorld>().ZoneSignature));
			Assert.That(closed.NextRepathTick, Is.EqualTo(Core<NavZoneTestWorld>.S.CurrentTick - 1 + closed.RepathIntervalTicks), "re-planned on the tick the zone closed");
			Assert.That(Corridor(closed).Any(zone.Contains), Is.False, "the new route avoids the closed zone");
		});

		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().Blocked = false;
		Step<NavZoneTestWorld>();
		var reopened = character.Read<NavAgent>();
		Assert.Multiple(() => {
			Assert.That(reopened.NextRepathTick, Is.EqualTo(Core<NavZoneTestWorld>.S.CurrentTick - 1 + reopened.RepathIntervalTicks), "re-planned on the tick the zone opened");
			Assert.That(Corridor(reopened).Any(zone.Contains), Is.True, "the short route is back");
		});
	}

	[Test]
	public void AgentReachesItsDestinationWithoutEnteringAClosedZone() {
		CreateWorld<NavZoneTestWorld>(Level());
		var character = TakeOverCharacter<NavZoneTestWorld>();
		var zone = GateTriangles();
		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().Blocked = true;
		character.Ref<NavAgent>().SetDestination(Point(4, 0));

		var enteredZone = false;
		for (var tick = 0; tick < 600 && character.Read<NavAgent>().Status != NavAgentStatus.Arrived; tick++) {
			Step<NavZoneTestWorld>();
			enteredZone |= zone.Contains(character.Read<NavAgent>().CurrentTriangle);
		}

		Assert.Multiple(() => {
			Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Arrived), $"{character.Read<NavAgent>().PathStatus}");
			Assert.That(enteredZone, Is.False);
		});
	}

	[Test]
	public void AgentInsideAZoneThatClosesWalksOut() {
		CreateWorld<NavZoneTestWorld>(Level());
		var character = TakeOverCharacter<NavZoneTestWorld>();
		var zone = GateTriangles();
		character.Ref<Transform>().Position = new F.FVector3(F.FP.FromRatio(4, 1), F.FP.FromRatio(3, 2), F.FP.FromRatio(-6, 1));
		ZoneEntity<NavZoneTestWorld>().Ref<NavZoneState>().Blocked = true;
		character.Ref<NavAgent>().SetDestination(Point(12, -6));

		Step<NavZoneTestWorld>();
		Assert.That(zone.Contains(character.Read<NavAgent>().CurrentTriangle), Is.True, "the agent starts inside the closed zone");
		Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Moving), $"{character.Read<NavAgent>().PathStatus}");

		for (var tick = 0; tick < 600 && character.Read<NavAgent>().Status != NavAgentStatus.Arrived; tick++) {
			Step<NavZoneTestWorld>();
		}
		Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Arrived));
	}

	[Test]
	public void RollbackAcrossZoneTogglesMatchesTheUninterruptedRun() {
		var reference = Record<NavZoneUninterruptedWorld>(SimulationType.ForwardOnly, inputDelay: 0);
		var rolledBack = Record<NavZoneRolledBackWorld>(SimulationType.AutomaticRollbacks, InputDelay);

		var divergedAt = -1;
		for (var tick = reference.StartTick; tick < reference.StartTick + Ticks; tick++) {
			if (!rolledBack.Hashes.TryGetValue(tick, out var hash) || hash != reference.Hashes[tick]
				|| rolledBack.Areas[tick] != reference.Areas[tick] || rolledBack.Corridors[tick] != reference.Corridors[tick]) {
				divergedAt = tick;
				break;
			}
		}
		var toggles = Enumerable.Range(reference.StartTick + 1, Ticks - 1)
			.Where(tick => ZoneToggleSystem<NavZoneUninterruptedWorld>.IsClosed(tick) != ZoneToggleSystem<NavZoneUninterruptedWorld>.IsClosed(tick - 1))
			.ToArray();

		Assert.Multiple(() => {
			Assert.That(toggles, Has.Length.GreaterThanOrEqualTo(2), "the run must close and reopen the zone");
			Assert.That(toggles.All(rolledBack.ResimulatedTicks.Contains), Is.True, "every toggle must be re-simulated");
			Assert.That(toggles.All(tick => reference.Replanned[tick]), Is.True, "the character re-plans on every toggle");
			Assert.That(reference.Areas.Values.Distinct().Count(), Is.EqualTo(2), "the mesh areas take the open and the closed state");
			Assert.That(divergedAt, Is.EqualTo(-1), "first tick whose world hash, areas, or corridor differs from the uninterrupted run");
		});
	}

	[Test]
	public void FullSyncWithAClosedZoneRederivesTheMesh() {
		CreateWorld<NavZoneSyncSourceWorld>(Level());
		AddPlayers<NavZoneSyncSourceWorld>();
		ZoneEntity<NavZoneSyncSourceWorld>().Ref<NavZoneState>().Blocked = true;
		for (var i = 0; i < 30; i++) {
			StepWithInput<NavZoneSyncSourceWorld>();
		}

		CreateWorld<NavZoneSyncTargetWorld>(Level());
		var sourceWriter = SnapshotWriter<NavZoneSyncSourceWorld>();
		new Core<NavZoneSyncSourceWorld>.GameWorldFullSyncHandler().WriteFullSync(ref sourceWriter);
		Core<NavZoneSyncTargetWorld>.S.HardReset(Core<NavZoneSyncSourceWorld>.S.CurrentTick);
		var reader = sourceWriter.AsReader();
		new Core<NavZoneSyncTargetWorld>.GameWorldFullSyncHandler().ReadFullSync(ref reader);

		var writerA = SnapshotWriter<NavZoneSyncSourceWorld>();
		var writerB = SnapshotWriter<NavZoneSyncTargetWorld>();
		for (var i = 0; i < 60; i++) {
			StepWithInput<NavZoneSyncSourceWorld>();
			StepWithInput<NavZoneSyncTargetWorld>();
			var tick = Core<NavZoneSyncSourceWorld>.S.CurrentTick;
			Assert.That(WorldHash<NavZoneSyncTargetWorld>(ref writerB), Is.EqualTo(WorldHash<NavZoneSyncSourceWorld>(ref writerA)), $"world hash after tick {tick}");
			Assert.That(AreaBytes(Navigation<NavZoneSyncTargetWorld>().Mesh!), Is.EqualTo(AreaBytes(Navigation<NavZoneSyncSourceWorld>().Mesh!)), $"areas after tick {tick}");
		}
		Assert.That(Navigation<NavZoneSyncTargetWorld>().IsZoneBlocked(0), Is.True);
	}

	private static NavigationRes Navigation<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		return Core<TWorld>.Systems.GetResource<NavigationRes>();
	}

	private static World<TWorld>.Entity ZoneEntity<TWorld>() where TWorld : struct, IWorldType {
		foreach (var entity in World<TWorld>.Query<All<NavZoneState>>().Entities()) {
			return entity;
		}
		throw new AssertionException("no zone entity was spawned");
	}

	private static HashSet<int> GateTriangles() {
		var navigation = Level().Navigation!;
		return [.. navigation.Zones.Single(static zone => zone.Id == "gate").Triangles];
	}

	private static int[] Corridor(in NavAgent agent) {
		ReadOnlySpan<int> corridor = agent.Corridor;
		return corridor[agent.CorridorIndex..agent.CorridorLength].ToArray();
	}

	private static byte[] AreaBytes(NavMesh mesh) {
		return MemoryMarshal.AsBytes(mesh.Areas).ToArray();
	}

	private static byte[] BakedAreaBytes() {
		return MemoryMarshal.AsBytes(Level().Navigation!.Mesh.Areas).ToArray();
	}

	/// <summary>The spawned character, detached from the chase behavior so the test sets its destination.</summary>
	private static World<TWorld>.Entity TakeOverCharacter<TWorld>() where TWorld : struct, IWorldType {
		var character = FindCharacter<TWorld>();
		character.Delete<ChasesNearestPlayer>();
		return character;
	}

	/// <summary>A destination on the ground surface at world (x, z).</summary>
	private static F.FVector3 Point(int x, int z) => new(F.FP.FromRatio(x, 1), F.FP.Half, F.FP.FromRatio(z, 1));

	/// <summary>Plays the scripted inputs with <see cref="ZoneToggleSystem{TWorld}"/>, delivering the remote player's <paramref name="inputDelay"/> ticks late.</summary>
	private static TickRecorder<TWorld> Record<TWorld>(SimulationType simulationType, int inputDelay) where TWorld : struct, IWorldType, ISessionType {
		CreateWorld<TWorld>(Level(), simulationType, static () => Core<TWorld>.Systems.Add(new ZoneToggleSystem<TWorld>(), order: 3));
		AddPlayers<TWorld>();
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
	/// The remote player (spawned right of the local one) walks toward the character and becomes its target, past the
	/// box's gate side, then back. The local player (x=0) walks left and stops.
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

	/// <summary>Records per simulated tick the world hash, the mesh areas, and the character's corridor.</summary>
	private sealed class TickRecorder<TWorld> : IRollbackObserver where TWorld : struct, IWorldType, ISessionType {
		private BinaryPackWriter _writer = SnapshotWriter<TWorld>();

		public TickRecorder(int startTick) {
			StartTick = startTick;
		}

		public int StartTick { get; }
		public Dictionary<int, ulong> Hashes { get; } = new();
		public Dictionary<int, string> Areas { get; } = new();
		public Dictionary<int, string> Corridors { get; } = new();

		/// <summary>Whether the character planned on the tick, as last simulated.</summary>
		public Dictionary<int, bool> Replanned { get; } = new();

		public HashSet<int> ResimulatedTicks { get; } = new();

		public void OnTickSimulated(int tick) {
			if (Hashes.ContainsKey(tick)) {
				ResimulatedTicks.Add(tick);
			}
			Hashes[tick] = WorldHash<TWorld>(ref _writer);
			Areas[tick] = Convert.ToHexString(AreaBytes(Navigation<TWorld>().Mesh!));
			var agent = FindCharacter<TWorld>().Read<NavAgent>();
			Corridors[tick] = string.Join(',', Corridor(agent));
			Replanned[tick] = agent.NextRepathTick == tick + agent.RepathIntervalTicks;
		}

		public void OnFullSync() { }
	}
}
