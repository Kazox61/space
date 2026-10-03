using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct DoorTestWorld : IWorldType, ISessionType;

// Hash-compared worlds are each created once per process; see NavRollbackTests.
public struct DoorUninterruptedWorld : IWorldType, ISessionType;

public struct DoorRolledBackWorld : IWorldType, ISessionType;

/// <summary>
/// Pressure plates, doors and navigation zones together, on the committed sample level. Its vault is
/// a walled room (inside x in [16, 24], z in [-4, 4]) whose only way in is a 3 m doorway in the west
/// wall at x=15.75, covered by the <c>vault</c> zone. The door fills the doorway when closed and
/// starts open, slid 3 m along +Z into the wall. One plate is just outside at (13, 0.5, 0);
/// another is inside at (18.5, 0.5, 2.5), offset from the walking path.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DoorTests {
	private const ushort LocalChannel = 0;
	private const ushort RemoteChannel = 1;
	private const int Ticks = 300;
	private const int InputDelay = 12;

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<DoorTestWorld>();
		DestroyIfCreated<DoorUninterruptedWorld>();
		DestroyIfCreated<DoorRolledBackWorld>();
	}

	[Test]
	public void EveryCharacterGetsOneSensorProxyThatFollowsIt() {
		CreateWorld<DoorTestWorld>(DoorLevel());
		var player = AddPlayer<DoorTestWorld>(LocalChannel);
		for (var i = 0; i < 30; i++) {
			StepWithInput<DoorTestWorld>(new PlayerInput { MoveX = F.FP.One }, default);
		}

		var proxies = 0;
		var followsPlayer = false;
		foreach (var proxy in World<DoorTestWorld>.Query<All<SensorProxy>>().Entities()) {
			proxies++;
			if (proxy.Read<SensorProxy>().Owner == player.GID) {
				var offset = proxy.Read<Body>().Transform.Position - player.Read<Transform>().ToWorldTransform().Position;
				followsPlayer = Fixed32.FVector3.Length(offset) < Fixed32.FP.FromRatio(1, 100);
			}
		}
		Assert.Multiple(() => {
			Assert.That(proxies, Is.EqualTo(2), "one for the player, one for the navigation character");
			Assert.That(followsPlayer, Is.True, "the proxy ends each tick where its character is");
		});

		player.Destroy();
		Step<DoorTestWorld>();
		var remaining = 0;
		foreach (var _ in World<DoorTestWorld>.Query<All<SensorProxy>>().Entities()) {
			remaining++;
		}
		Assert.That(remaining, Is.EqualTo(1), "a destroyed character's proxy goes with it");
	}

	[Test]
	public void SteppingOnThePlateTogglesTheDoorAndItsZone() {
		CreateWorld<DoorTestWorld>(DoorLevel());
		TakeOverCharacter<DoorTestWorld>();
		var player = AddPlayer<DoorTestWorld>(LocalChannel);
		StandStill<DoorTestWorld>();
		Assert.Multiple(() => {
			Assert.That(DoorState<DoorTestWorld>().FullyOpen, Is.True, "the vault door starts open");
			Assert.That(VaultZone<DoorTestWorld>().Blocked, Is.False);
		});

		MoveTo(player, OutsidePlate);
		StandStill<DoorTestWorld>();
		Assert.Multiple(() => {
			Assert.That(Occupants<DoorTestWorld>(), Is.EqualTo(1));
			Assert.That(DoorState<DoorTestWorld>().Open, Is.False, "stepping on closes the open door");
		});
		StandStill<DoorTestWorld>();
		Assert.That(VaultZone<DoorTestWorld>().Blocked, Is.True, "the zone closes as soon as the door starts closing");

		StandFor<DoorTestWorld>(120);
		Assert.Multiple(() => {
			Assert.That(DoorState<DoorTestWorld>().Open, Is.False, "standing on the plate does not toggle it again");
			Assert.That(Distance(DoorPosition<DoorTestWorld>(), ClosedDoor), Is.LessThan(0.01), "the door reached its closed position");
			Assert.That(VaultZone<DoorTestWorld>().Blocked, Is.True);
		});

		MoveTo(player, Spawn);
		StandStill<DoorTestWorld>();
		Assert.Multiple(() => {
			Assert.That(Occupants<DoorTestWorld>(), Is.Zero);
			Assert.That(DoorState<DoorTestWorld>().Open, Is.False, "stepping off does nothing");
		});

		MoveTo(player, OutsidePlate);
		StandStill<DoorTestWorld>();
		Assert.That(DoorState<DoorTestWorld>().Open, Is.True, "stepping on again opens it");
		StandStill<DoorTestWorld>();
		Assert.That(VaultZone<DoorTestWorld>().Blocked, Is.True, "the zone stays closed until the door is fully open");

		StandFor<DoorTestWorld>(120);
		Assert.Multiple(() => {
			Assert.That(DoorState<DoorTestWorld>().FullyOpen, Is.True);
			Assert.That(Distance(DoorPosition<DoorTestWorld>(), OpenDoor), Is.LessThan(0.01), "the door reached its open position");
			Assert.That(VaultZone<DoorTestWorld>().Blocked, Is.False);
		});
	}

	[Test]
	public void AgentReachesTheVaultOnlyWhileItsDoorIsOpen() {
		CreateWorld<DoorTestWorld>(DoorLevel());
		var character = TakeOverCharacter<DoorTestWorld>();
		var player = AddPlayer<DoorTestWorld>(LocalChannel);
		var doorway = VaultTriangles();
		var insideVault = new F.FVector3(F.FP.FromRatio(20, 1), F.FP.Half, F.FP.Zero);

		// Close the door before the agent sets off.
		MoveTo(player, OutsidePlate);
		StandFor<DoorTestWorld>(120);
		Assert.That(DoorState<DoorTestWorld>().Open, Is.False);

		character.Ref<NavAgent>().SetDestination(insideVault);
		StandFor<DoorTestWorld>(120);
		Assert.Multiple(() => {
			Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Failed), "the closed door seals the room");
			Assert.That(ToDouble(character.Read<Transform>().Position.X), Is.LessThan(15), "the agent stays outside");
		});

		// Step off and on again: the door opens and the agent walks in through the doorway.
		MoveTo(player, Spawn);
		StandStill<DoorTestWorld>();
		MoveTo(player, OutsidePlate);
		var crossedDoorway = false;
		for (var tick = 0; tick < 900 && character.Read<NavAgent>().Status != NavAgentStatus.Arrived; tick++) {
			StandStill<DoorTestWorld>();
			crossedDoorway |= doorway.Contains(character.Read<NavAgent>().CurrentTriangle);
		}
		Assert.Multiple(() => {
			Assert.That(DoorState<DoorTestWorld>().FullyOpen, Is.True);
			Assert.That(character.Read<NavAgent>().Status, Is.EqualTo(NavAgentStatus.Arrived), $"{character.Read<NavAgent>().PathStatus}");
			Assert.That(crossedDoorway, Is.True, "the only way in is through the doorway");
		});
	}

	[Test]
	public void ClosingDoorPushesACharacterOutOfItsWay() {
		// Open ground and the door alone, so nothing but the door acts on the character.
		var ground = PhysicsSmokeTest.TestLevels.Arena.Entities.Single(static entity => entity.SourcePath == "Arena/Ground");
		var door = PhysicsSmokeTest.TestLevels.Door(
			"Door",
			new FWorldTransform(new FPos(F.FP.FromRatio(4, 1), F.FP.FromRatio(3, 2), F.FP.FromRatio(-6, 1)), Fixed32.FQuaternion.Identity),
			new Fixed32.FVector3(Fixed32.FP.Two, Fixed32.FP.One, Fixed32.FP.Quarter),
			"gate",
			new Fixed32.FVector3(Fixed32.FP.FromRatio(5, 1), Fixed32.FP.Zero, Fixed32.FP.Zero),
			Fixed32.FP.Two,
			startsOpen: true);
		var gate = new NavZoneVolume("gate", door.Transform, new Fixed32.FVector3(Fixed32.FP.Two, Fixed32.FP.One, Fixed32.FP.FromRatio(3, 1)));
		CreateWorld<DoorTestWorld>(new LevelData([ground, door], navZones: [gate]));
		var player = AddPlayer<DoorTestWorld>(LocalChannel);
		player.Ref<Transform>().Position = new F.FVector3(F.FP.FromRatio(5, 1), F.FP.FromRatio(3, 2), F.FP.FromRatio(-6, 1));
		Step<DoorTestWorld>();
		DoorEntity<DoorTestWorld>().Ref<DoorState>().Open = false;

		// The door's leading (-X) face travels from x=7 to x=2; the capsule (radius 0.5) must stay ahead of it.
		var deepestPenetration = 0.0;
		for (var i = 0; i < 200; i++) {
			Step<DoorTestWorld>();
			var face = DoorPosition<DoorTestWorld>().X - 2;
			var capsuleRight = ToDouble(player.Read<Transform>().Position.X) + 0.5;
			deepestPenetration = Math.Max(deepestPenetration, capsuleRight - face);
		}

		Assert.Multiple(() => {
			Assert.That(Distance(DoorPosition<DoorTestWorld>(), new Vector(4, 1.5, -6)), Is.LessThan(0.01), "the character did not stop the door");
			Assert.That(ToDouble(player.Read<Transform>().Position.X), Is.LessThanOrEqualTo(1.55), "pushed clear of the closed door");
			Assert.That(ToDouble(player.Read<Transform>().Position.Z), Is.EqualTo(-6).Within(0.1), "pushed straight along the door's travel");
			Assert.That(ToDouble(player.Read<Transform>().Position.Y), Is.EqualTo(1.5).Within(0.05), "still standing on the ground");
			Assert.That(deepestPenetration, Is.LessThan(0.1), "the mover resolves each tick's push");
		});
	}

	[Test]
	public void RollbackAcrossPlateStepsMatchesTheUninterruptedRun() {
		var reference = Record<DoorUninterruptedWorld>(SimulationType.ForwardOnly, inputDelay: 0);
		var rolledBack = Record<DoorRolledBackWorld>(SimulationType.AutomaticRollbacks, InputDelay);

		var divergedAt = -1;
		for (var tick = reference.StartTick; tick < reference.StartTick + Ticks; tick++) {
			if (!rolledBack.Hashes.TryGetValue(tick, out var hash) || hash != reference.Hashes[tick]) {
				divergedAt = tick;
				break;
			}
		}
		var toggles = reference.DoorOpen.Keys
			.Where(tick => reference.DoorOpen.TryGetValue(tick - 1, out var before) && before != reference.DoorOpen[tick])
			.Order()
			.ToArray();

		Assert.Multiple(() => {
			Assert.That(toggles, Has.Length.EqualTo(2), "the remote player closes the door and opens it again");
			Assert.That(toggles.All(rolledBack.ResimulatedTicks.Contains), Is.True, "every plate step must be re-simulated");
			Assert.That(rolledBack.DoorOpen.Keys.Count(tick => rolledBack.FirstDoorOpen[tick] != rolledBack.DoorOpen[tick]), Is.GreaterThan(0),
				"the prediction missed a plate step, so only the re-simulation sent its sensor event");
			Assert.That(divergedAt, Is.EqualTo(-1), "first tick whose world hash differs from the uninterrupted run");
		});
	}

	private static readonly Vector ClosedDoor = new(15.75, 1.5, 0);
	private static readonly Vector OpenDoor = ClosedDoor + new Vector(0, 0, 3);
	private static readonly Vector OutsidePlate = new(13, 1.5, 0);
	private static readonly Vector Spawn = new(0, 1.5, 0);

	private readonly record struct Vector(double X, double Y, double Z) {
		public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
	}

	private static double ToDouble(F.FP value) => F.FConversions.ToDouble(value);

	private static double Distance(Vector a, Vector b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

	private static Vector DoorPosition<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		var position = DoorEntity<TWorld>().Read<Body>().Transform.Position;
		return new Vector(ToDouble(position.X), ToDouble(position.Y), ToDouble(position.Z));
	}

	private static World<TWorld>.Entity DoorEntity<TWorld>() where TWorld : struct, IWorldType {
		foreach (var entity in World<TWorld>.Query<All<DoorState>>().Entities()) {
			return entity;
		}
		throw new AssertionException("no door was spawned");
	}

	private static DoorState DoorState<TWorld>() where TWorld : struct, IWorldType => DoorEntity<TWorld>().Read<DoorState>();

	/// <summary>Characters standing on any plate.</summary>
	private static int Occupants<TWorld>() where TWorld : struct, IWorldType {
		var occupants = 0;
		foreach (var entity in World<TWorld>.Query<All<PressurePlateState>>().Entities()) {
			occupants += entity.Read<PressurePlateState>().Occupants;
		}
		return occupants;
	}

	private static NavZoneState VaultZone<TWorld>() where TWorld : struct, IWorldType {
		var zone = DoorState<TWorld>().Zone;
		foreach (var entity in World<TWorld>.Query<All<NavZoneState>>().Entities()) {
			if (entity.Read<NavZoneState>().Zone == zone) {
				return entity.Read<NavZoneState>();
			}
		}
		throw new AssertionException("no zone entity for the door");
	}

	/// <summary>Teleports a character (a standing capsule, so y=1.5 is on the ground); its proxy follows the jump.</summary>
	private static void MoveTo<TWorld>(World<TWorld>.Entity character, Vector position) where TWorld : struct, IWorldType {
		character.Ref<Transform>().Position = new F.FVector3(
			F.FConversions.ToFP(position.X), F.FConversions.ToFP(position.Y), F.FConversions.ToFP(position.Z));
	}

	private static HashSet<int> VaultTriangles() {
		return [.. DoorLevel().Navigation!.Zones.Single(static zone => zone.Id == "vault").Triangles];
	}

	/// <summary>The spawned character, detached from the chase behavior so it cannot wander onto the plate.</summary>
	private static World<TWorld>.Entity TakeOverCharacter<TWorld>() where TWorld : struct, IWorldType {
		var character = FindCharacter<TWorld>();
		character.Delete<ChasesNearestPlayer>();
		return character;
	}

	private static World<TWorld>.Entity AddPlayer<TWorld>(ushort channel) where TWorld : struct, IWorldType, ISessionType {
		return Core<TWorld>.W.NewEntity(new Player { PlayerGuid = new Guid(channel + 1, 0, 0, new byte[8]), InputChannel = channel });
	}

	private static void StepWithInput<TWorld>(PlayerInput local, PlayerInput remote) where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.S.SetApprovedInput(LocalChannel, local);
		Core<TWorld>.S.SetApprovedInput(RemoteChannel, remote);
		Step<TWorld>();
	}

	/// <summary>One tick with no input; a bare step would repeat the last input.</summary>
	private static void StandStill<TWorld>() where TWorld : struct, IWorldType, ISessionType => StepWithInput<TWorld>(default, default);

	private static void StandFor<TWorld>(int ticks) where TWorld : struct, IWorldType, ISessionType {
		for (var i = 0; i < ticks; i++) {
			StandStill<TWorld>();
		}
	}

	/// <summary>Plays the scripted inputs, delivering the remote player's <paramref name="inputDelay"/> ticks late.</summary>
	private static TickRecorder<TWorld> Record<TWorld>(SimulationType simulationType, int inputDelay) where TWorld : struct, IWorldType, ISessionType {
		CreateWorld<TWorld>(DoorLevel(), simulationType);
		TakeOverCharacter<TWorld>();
		AddPlayer<TWorld>(LocalChannel);
		AddPlayer<TWorld>(RemoteChannel);
		Core<TWorld>.S.SaveFrame();
		var start = Core<TWorld>.S.CurrentTick;
		var recorder = new TickRecorder<TWorld>(start);
		Core<TWorld>.RollbackObserver = recorder;

		var end = start + Ticks;
		for (var tick = start; tick < end + inputDelay; tick++) {
			var remoteTick = tick - inputDelay;
			if (remoteTick >= start && remoteTick < end) {
				Core<TWorld>.S.SetApprovedInputAt(remoteTick, RemoteChannel, ScriptedRemoteInput(remoteTick - start));
			}
			if (tick < end) {
				Core<TWorld>.S.SetApprovedInput(LocalChannel, default(PlayerInput));
				Step<TWorld>();
			} else {
				Core<TWorld>.S.FastForwardToTick(end);
			}
		}
		Assert.That(Core<TWorld>.S.CurrentTick, Is.EqualTo(end));
		return recorder;
	}

	/// <summary>
	/// The remote player (spawned at x=3, walking 7 m/s) stops with its capsule 0.7 m short of the
	/// plate (its near edge is at x=12), then steps on about six ticks after walking on: less than the input
	/// delay, so the prediction (still standing) misses the step. It waits, walks left off the plate,
	/// then right onto it again. The local player stands still at x=0.
	/// </summary>
	private static PlayerInput ScriptedRemoteInput(int tick) {
		return tick switch {
			>= 10 and < 77 => new PlayerInput { MoveX = F.FP.One },
			>= 110 and < 130 => new PlayerInput { MoveX = F.FP.One },
			>= 170 and < 210 => new PlayerInput { MoveX = -F.FP.One },
			>= 230 and < 270 => new PlayerInput { MoveX = F.FP.One },
			_ => default,
		};
	}

	/// <summary>Records per simulated tick the world hash and whether the door is heading open.</summary>
	private sealed class TickRecorder<TWorld> : IRollbackObserver where TWorld : struct, IWorldType, ISessionType {
		private BinaryPackWriter _writer = SnapshotWriter<TWorld>();

		public TickRecorder(int startTick) {
			StartTick = startTick;
		}

		public int StartTick { get; }
		public Dictionary<int, ulong> Hashes { get; } = new();
		public Dictionary<int, bool> DoorOpen { get; } = new();
		public Dictionary<int, bool> FirstDoorOpen { get; } = new();
		public HashSet<int> ResimulatedTicks { get; } = new();

		public void OnTickSimulated(int tick) {
			if (Hashes.ContainsKey(tick)) {
				ResimulatedTicks.Add(tick);
			}
			Hashes[tick] = WorldHash<TWorld>(ref _writer);
			var open = DoorState<TWorld>().Open;
			FirstDoorOpen.TryAdd(tick, open);
			DoorOpen[tick] = open;
		}

		public void OnFullSync() { }
	}
}
