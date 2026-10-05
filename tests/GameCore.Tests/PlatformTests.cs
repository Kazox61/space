using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using Shenanicode.Rollback;
using Space.NavBuilder;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct PlatformTestWorld : IWorldType, ISessionType;
public struct PlatformForwardWorld : IWorldType, ISessionType;
public struct PlatformRollbackWorld : IWorldType, ISessionType;

[TestFixture]
[NonParallelizable]
public sealed class PlatformTests {
	private const ushort LocalChannel = 0;
	private const ushort Channel = 1;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<PlatformTestWorld>();
		DestroyIfCreated<PlatformForwardWorld>();
		DestroyIfCreated<PlatformRollbackWorld>();
	}

	[Test]
	public void StandingCharacterRidesPlatformAndKeepsItsVelocityOnJump() {
		CreateWorld<PlatformTestWorld>(PlatformLevel());
		var player = Core<PlatformTestWorld>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = Channel });
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(9, 4), F.FP.Zero);

		for (var i = 0; i < 270; i++) {
			StepStill();
		}

		var platform = PlatformEntity();
		var relativeX = ToDouble(player.Read<Transform>().Position.X - platform.Read<Transform>().Position.X);
		Assert.Multiple(() => {
			Assert.That(ToDouble(platform.Read<Transform>().Position.X), Is.GreaterThan(1.4));
			Assert.That(Math.Abs(relativeX), Is.LessThan(0.08), "a standing mover stays over the same point on the platform");
			Assert.That(player.Read<Mover>().Grounded, Is.True);
			Assert.That(player.Read<Mover>().SupportVelocity.X, Is.EqualTo(FP.One));
		});

		Core<PlatformTestWorld>.S.SetApprovedInput(Channel, new PlayerInput { Jump = true });
		Step<PlatformTestWorld>();
		var jumpX = player.Read<Transform>().Position.X;
		for (var i = 0; i < 20; i++) {
			StepStill();
		}

		Assert.Multiple(() => {
			Assert.That(player.Read<Mover>().Grounded, Is.False);
			Assert.That(player.Read<Mover>().InheritedVelocity.X, Is.EqualTo(FP.One));
			Assert.That(ToDouble(player.Read<Transform>().Position.X - jumpX), Is.GreaterThan(0.25), "the jump retains platform momentum");
		});
	}

	[Test]
	public void CharacterRidesVerticalPlatformThroughAscentDescentAndReversal() {
		CreateWorld<PlatformTestWorld>(VerticalPlatformLevel());
		var player = Core<PlatformTestWorld>.W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = Channel });
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(9, 4), F.FP.Zero);
		var platform = PlatformEntity();
		var maximumRelativeError = 0.0;
		var sawAscent = false;
		var sawDescent = false;
		var lostGroundWhileMoving = false;

		for (var tick = 0; tick < 700; tick++) {
			StepStill();
			var velocity = platform.Read<Body>().LinearVelocity.Y;
			sawAscent |= velocity > FP.Zero;
			sawDescent |= velocity < FP.Zero;
			lostGroundWhileMoving |= velocity != FP.Zero && !player.Read<Mover>().Grounded;
			if (player.Read<Mover>().Grounded) {
				var relativeY = ToDouble(player.Read<Transform>().Position.Y - platform.Read<Transform>().Position.Y);
				maximumRelativeError = Math.Max(maximumRelativeError, Math.Abs(relativeY - 1.25));
			}
		}

		Assert.Multiple(() => {
			Assert.That(sawAscent, Is.True);
			Assert.That(sawDescent, Is.True);
			Assert.That(lostGroundWhileMoving, Is.False);
			Assert.That(player.Read<Mover>().Grounded, Is.True);
			Assert.That(maximumRelativeError, Is.LessThan(0.08), "the rider must not lag, jitter, or detach at either reversal");
		});
	}

	[Test]
	public void LinkedZoneIsBlockedWhilePlatformIsAwayFromItsAuthoredFloor() {
		CreateWorld<PlatformTestWorld>(PlatformLevel());
		var zoneEntity = ZoneEntity<PlatformTestWorld>();
		var navigation = Core<PlatformTestWorld>.Systems.GetResource<NavigationRes>();

		Assert.Multiple(() => {
			Assert.That(zoneEntity.Read<NavZoneState>().Blocked, Is.False, "the platform starts aligned with the linked floor");
			Assert.That(navigation.IsZoneBlocked(0), Is.False);
		});

		for (var tick = 0; tick < 3 * 60; tick++) {
			StepStill();
			Assert.That(zoneEntity.Read<NavZoneState>().Blocked, Is.False, $"dwell tick {tick}");
			Assert.That(PlatformEntity().Read<Body>().LinearVelocity, Is.EqualTo(FVector3.Zero), $"dwell tick {tick}");
		}
		StepStill();
		Assert.Multiple(() => {
			Assert.That(zoneEntity.Read<NavZoneState>().Blocked, Is.True, "the shaft closes once the platform leaves");
			Assert.That(navigation.IsZoneBlocked(0), Is.True);
		});

		var reopened = false;
		for (var i = 0; i < 900; i++) {
			StepStill();
			var platformAligned = FVector3.Length(PlatformEntity().Read<Body>().Transform.Position - PlatformEntity().Read<PatrolRail>().Start) <= FP.FromRatio(1, 1000);
			Assert.That(zoneEntity.Read<NavZoneState>().Blocked, Is.EqualTo(!platformAligned), $"tick {i}");
			Assert.That(navigation.IsZoneBlocked(0), Is.EqualTo(!platformAligned), $"derived navigation at tick {i}");
			reopened |= platformAligned;
		}
		Assert.That(reopened, Is.True, "the zone reopens when the rail returns to the authored floor");
	}

	[Test]
	public void PlatformBakeSourceDoesNotSpawnADuplicateStaticBody() {
		CreateWorld<PlatformTestWorld>(BridgePlatformLevel());
		var staticBodies = 0;
		var linkedPlatforms = 0;
		foreach (var entity in Core<PlatformTestWorld>.W.Query<All<Body>>().Entities()) {
			if (entity.Read<Body>().Type == BodyType.Static) {
				staticBodies++;
			}
			if (entity.Has<PatrolRail>() && entity.Read<PatrolRail>().Zone >= 0) {
				linkedPlatforms++;
			}
		}

		Assert.Multiple(() => {
			Assert.That(staticBodies, Is.EqualTo(2), "only the two banks spawn static collision");
			Assert.That(linkedPlatforms, Is.EqualTo(1), "the platform's bake source reuses its moving body");
		});
	}

	[Test]
	public void AnyPlatformAwayBlocksASharedZone() {
		CreateWorld<PlatformTestWorld>(PlatformLevel());
		var first = PlatformEntity();
		ref readonly var firstRail = ref first.Read<PatrolRail>();
		var second = Core<PlatformTestWorld>.W.NewEntity<Default>();
		var endTransform = new FWorldTransform(firstRail.End, FQuaternion.Identity);
		second.Set(new PatrolRail {
			Start = firstRail.Start,
			End = firstRail.End,
			Speed = firstRail.Speed,
			MovingToEnd = false,
			Zone = firstRail.Zone,
		});
		Core<PlatformTestWorld>.BodyOperations.CreateBody(second, BodyType.Kinematic, endTransform);

		StepStill();

		Assert.That(ZoneEntity<PlatformTestWorld>().Read<NavZoneState>().Blocked, Is.True);
	}

	[Test]
	public void RollbackAcrossAPlatformJumpMatchesTheUninterruptedRun() {
		var reference = Record<PlatformForwardWorld>(SimulationType.ForwardOnly, inputDelay: 0);
		var corrected = Record<PlatformRollbackWorld>(SimulationType.AutomaticRollbacks, inputDelay: 12);
		var firstMismatch = reference.Hashes.Keys.Order().FirstOrDefault(tick => corrected.Hashes[tick] != reference.Hashes[tick], -1);

		Assert.Multiple(() => {
			Assert.That(corrected.ResimulatedTicks, Is.Not.Empty, "late jump input triggers re-simulation");
			Assert.That(corrected.FirstPlayerY.Any(pair => pair.Value != corrected.PlayerY[pair.Key]), Is.True,
				"the predicted rider position is corrected on the moving floor");
			Assert.That(firstMismatch, Is.EqualTo(-1), "corrected world hash matches uninterrupted simulation");
		});
	}

	[Test]
	public void AgentCrossesPlatformDuringDwellAndNeverEntersWhileItIsAway() {
		CreateWorld<PlatformTestWorld>(BridgePlatformLevel(), registerSystems: () => {
			Core<PlatformTestWorld>.Systems.GetResource<NavCharacterRes>().SpawnPosition = new F.FVector3(F.FP.FromRatio(-7, 2), F.FP.FromRatio(3, 2), F.FP.Zero);
		});
		var follower = FindCharacter<PlatformTestWorld>();
		follower.Delete<ChasesNearestPlayer>();
		follower.Ref<Transform>().Position = new F.FVector3(F.FP.FromRatio(-7, 2), F.FP.FromRatio(3, 2), F.FP.Zero);
		var navigation = Core<PlatformTestWorld>.Systems.GetResource<NavigationRes>();
		var elevator = navigation.FindZone("shaft");
		HashSet<int> elevatorTriangles = [.. navigation.Zones[elevator].Triangles];
		var enteredWhileBlocked = false;
		var enteredAtAll = false;
		follower.Ref<NavAgent>().SetDestination(new F.FVector3(F.FP.FromRatio(7, 2), F.FP.Half, F.FP.Zero));

		for (var tick = 0; tick < 900 && follower.Read<NavAgent>().Status != NavAgentStatus.Arrived; tick++) {
			StepStill();
			var inside = elevatorTriangles.Contains(follower.Read<NavAgent>().CurrentTriangle);
			enteredAtAll |= inside;
			enteredWhileBlocked |= navigation.IsZoneBlocked(elevator) && inside;
		}

		Assert.Multiple(() => {
			Assert.That(enteredWhileBlocked, Is.False,
				"the chase agent must not enter the empty elevator footprint while its navigation zone is blocked");
			Assert.That(enteredAtAll, Is.True, "the agent traverses the platform-backed navigation zone during endpoint dwell");
			Assert.That(follower.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Arrived), $"{follower.Read<NavAgent>().PathStatus}");
		});
	}

	private static LevelData? s_platformLevel;

	private static LevelData PlatformLevel() {
		if (s_platformLevel is not null) {
			return s_platformLevel;
		}
		var ground = PhysicsSmokeTest.TestLevels.StaticBox(
			"Ground",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.Zero, F.FP.Zero), FQuaternion.Identity),
			new FVector3(20.ToFP(), FP.Half, 20.ToFP())
		);
		var platform = PhysicsSmokeTest.TestLevels.Platform(
			"Platform",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.One, F.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.Quarter, FP.Two),
			"shaft",
			new FVector3(4.ToFP(), FP.Zero, FP.Zero),
			FP.One
		);
		var zone = new NavZoneVolume(
			"shaft",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.One, F.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.One, FP.Two)
		);
		return s_platformLevel = NavMeshBaker.BakeLevel(new LevelData([ground, platform], navZones: [zone]), NavBakeSettings.Default, out _);
	}

	private static LevelData? s_verticalPlatformLevel;

	private static LevelData VerticalPlatformLevel() {
		if (s_verticalPlatformLevel is not null) {
			return s_verticalPlatformLevel;
		}
		var ground = PhysicsSmokeTest.TestLevels.StaticBox(
			"Ground",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.Zero, F.FP.Zero), FQuaternion.Identity),
			new FVector3(20.ToFP(), FP.Half, 20.ToFP())
		);
		var platform = PhysicsSmokeTest.TestLevels.Platform(
			"Platform",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.One, F.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.Quarter, FP.Two),
			"shaft",
			new FVector3(FP.Zero, 4.ToFP(), FP.Zero),
			6.ToFP()
		);
		var zone = new NavZoneVolume("shaft", platform.Transform, new FVector3(FP.Two, FP.One, FP.Two));
		return s_verticalPlatformLevel = NavMeshBaker.BakeLevel(new LevelData([ground, platform], navZones: [zone]), NavBakeSettings.Default, out _);
	}

	private static LevelData? s_bridgePlatformLevel;

	private static LevelData BridgePlatformLevel() {
		if (s_bridgePlatformLevel is not null) {
			return s_bridgePlatformLevel;
		}
		var platform = PhysicsSmokeTest.TestLevels.Platform(
			"Platform",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.Zero, F.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.Half, 3.ToFP()),
			"shaft",
			new FVector3(FP.Zero, 4.ToFP(), FP.Zero),
			FP.One
		);
		var zone = new NavZoneVolume(
			"shaft",
			new FWorldTransform(new FPos(F.FP.Zero, F.FP.Half, F.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.One, 3.ToFP())
		);
		return s_bridgePlatformLevel = NavMeshBaker.BakeLevel(new LevelData([
			PhysicsSmokeTest.TestLevels.StaticBox("Left", new FWorldTransform(new FPos(F.FP.FromRatio(-6, 1), F.FP.Zero, F.FP.Zero), FQuaternion.Identity), new FVector3(4.ToFP(), FP.Half, 3.ToFP())),
			platform,
			PhysicsSmokeTest.TestLevels.StaticBox("Right", new FWorldTransform(new FPos(F.FP.FromRatio(6, 1), F.FP.Zero, F.FP.Zero), FQuaternion.Identity), new FVector3(4.ToFP(), FP.Half, 3.ToFP())),
		], navZones: [zone]), NavBakeSettings.Default, out _);
	}

	private static World<PlatformTestWorld>.Entity PlatformEntity() {
		foreach (var entity in World<PlatformTestWorld>.Query<All<PatrolRail>>().Entities()) {
			if (entity.Read<PatrolRail>().Zone == 0) {
				return entity;
			}
		}
		throw new AssertionException("no platform was spawned");
	}

	private static World<TWorld>.Entity ZoneEntity<TWorld>() where TWorld : struct, IWorldType {
		foreach (var entity in World<TWorld>.Query<All<NavZoneState>>().Entities()) {
			return entity;
		}
		throw new AssertionException("no navigation zone was spawned");
	}

	private static RollbackRecorder<TWorld> Record<TWorld>(SimulationType simulationType, int inputDelay)
		where TWorld : struct, IWorldType, ISessionType {
		CreateWorld<TWorld>(PlatformLevel(), simulationType);
		var player = Core<TWorld>.W.NewEntity(new Player { PlayerGuid = new Guid(2, 0, 0, new byte[8]), InputChannel = Channel });
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(9, 4), F.FP.Zero);
		Core<TWorld>.S.SaveFrame();
		var start = Core<TWorld>.S.CurrentTick;
		var recorder = new RollbackRecorder<TWorld>(player.GID);
		Core<TWorld>.RollbackObserver = recorder;

		const int ticks = 120;
		for (var tick = start; tick < start + ticks + inputDelay; tick++) {
			var inputTick = tick - inputDelay;
			if (inputTick >= start && inputTick < start + ticks) {
				Core<TWorld>.S.SetApprovedInputAt(inputTick, Channel, ScriptedInput(inputTick - start));
			}
			if (tick < start + ticks) {
				Core<TWorld>.S.SetApprovedInput(LocalChannel, default(PlayerInput));
				Step<TWorld>();
			} else {
				Core<TWorld>.S.FastForwardToTick(start + ticks);
			}
		}
		return recorder;
	}

	private static PlayerInput ScriptedInput(int tick) => tick == 45 ? new PlayerInput { Jump = true } : default;

	private sealed class RollbackRecorder<TWorld>(EntityGID player) : IRollbackObserver
		where TWorld : struct, IWorldType, ISessionType {
		private FFS.Libraries.StaticPack.BinaryPackWriter _writer = SnapshotWriter<TWorld>();

		public Dictionary<int, ulong> Hashes { get; } = new();
		public Dictionary<int, F.FP> PlayerY { get; } = new();
		public Dictionary<int, F.FP> FirstPlayerY { get; } = new();
		public HashSet<int> ResimulatedTicks { get; } = new();

		public void OnTickSimulated(int tick) {
			if (Hashes.ContainsKey(tick)) {
				ResimulatedTicks.Add(tick);
			}
			Hashes[tick] = WorldHash<TWorld>(ref _writer);
			if (player.TryUnpack<TWorld>(out var entity)) {
				var y = entity.Read<Transform>().Position.Y;
				FirstPlayerY.TryAdd(tick, y);
				PlayerY[tick] = y;
			}
		}

		public void OnFullSync() { }
	}

	private static void StepStill() {
		Core<PlatformTestWorld>.S.SetApprovedInput(Channel, default(PlayerInput));
		Step<PlatformTestWorld>();
	}

	private static double ToDouble(F.FP value) => F.FConversions.ToDouble(value);
}
