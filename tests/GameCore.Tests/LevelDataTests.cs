using System.Security.Cryptography;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using Shenanicode.Rollback;
using Space.NavBuilder;
using static Space.GameCore.Core<Space.GameCore.Tests.LevelTestWorld>;

namespace Space.GameCore.Tests;

public struct LevelTestWorld : IWorldType, ISessionType;

[TestFixture]
[NonParallelizable]
public sealed class LevelDataTests {
	[Test]
	public void CodecRoundTripsDeterministically() {
		var a = Placement("Map/A", 250, LootKind.Ammo, Fixed64.FP.FromRatio(2, 1));
		var b = Placement("Map/B", 40, LootKind.Health, Fixed64.FP.FromRatio(-1, 1));
		var forward = LevelDataCodec.Serialize(new LevelData([a, b]));
		var reversed = LevelDataCodec.Serialize(new LevelData([b, a]));

		Assert.That(reversed, Is.EqualTo(forward));
		var decoded = LevelDataCodec.Deserialize(forward);
		Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(forward));
		Assert.That(decoded.Entities, Has.Count.EqualTo(2));
		Assert.Multiple(() => {
			Assert.That(decoded.Entities[0].SourcePath, Is.EqualTo("Map/A"));
			Assert.That(decoded.Entities[0].Components.Health, Is.EqualTo(250));
			Assert.That(decoded.Entities[0].Components.Loot, Is.EqualTo(LootKind.Ammo));
			Assert.That(decoded.Entities[0].Components.Navigation, Is.Null);
			Assert.That(decoded.Entities[0].Transform.Position.X, Is.EqualTo(a.Transform.Position.X));
			Assert.That(decoded.Entities[1].Components.Health, Is.EqualTo(40));
		});
	}

	[Test]
	public void CodecRejectsTamperedPayload() {
		var bytes = LevelDataCodec.Serialize(new LevelData([Placement("Map/A", 100, LootKind.None, Fixed64.FP.Zero)]));
		bytes[^1] ^= 0x01;
		Assert.That(() => LevelDataCodec.Deserialize(bytes), Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void CodecRejectsUnknownVersion() {
		var bytes = LevelDataCodec.Serialize(new LevelData([Placement("Map/A", 100, LootKind.None, Fixed64.FP.Zero)]));
		bytes[4] = 99;
		Assert.That(() => LevelDataCodec.Deserialize(bytes), Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void CodecReadsVersion7EntitiesZonesAndNavigation() {
		var zone = new NavZoneVolume(
			"gate",
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Half, Fixed64.FP.Zero), FQuaternion.Identity),
			new FVector3(FP.Two, FP.One, FP.Two));
		var source = NavMeshBaker.BakeLevel(
			new LevelData([
				PhysicsSmokeTest.TestLevels.StaticBox(
					"Map/Ground",
					new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
					new FVector3(10.ToFP(), FP.Half, 10.ToFP()))
			], navZones: [zone]),
			NavBakeSettings.Default,
			out _);
		// Version 7 and 8 have identical envelopes and payloads when the new RailMotion bit is absent.
		var bytes = LevelDataCodec.Serialize(source);
		bytes[sizeof(uint)] = 7;
		bytes[sizeof(uint) + 1] = 0;

		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.Multiple(() => {
			Assert.That(decoded.Entities, Is.EqualTo(source.Entities));
			Assert.That(decoded.NavZones, Is.EqualTo(source.NavZones));
			Assert.That(decoded.Navigation, Is.Not.Null);
			Assert.That(decoded.Navigation!.Mesh.Vertices.ToArray(), Is.EqualTo(source.Navigation!.Mesh.Vertices.ToArray()));
			Assert.That(decoded.Navigation.Zones.Single().Id, Is.EqualTo("gate"));
			Assert.That(decoded.Navigation.Zones.Single().Triangles.ToArray(), Is.EqualTo(source.Navigation.Zones.Single().Triangles.ToArray()));
		});
	}

	[Test]
	public void CodecRejectsOverlongSourcePathBeforeWriting() {
		var placement = Placement(new string('x', 1025), 100, LootKind.None, Fixed64.FP.Zero);
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([placement])), Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void CodecRoundTripsStaticGeometry() {
		var level = new LevelData([
			Box("Map/WallB", 3, NavContribution.Excluded),
			Box("Map/WallA", -5, NavContribution.ObstacleOnly)
		]);
		var bytes = LevelDataCodec.Serialize(level);
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
		Assert.That(decoded.Entities, Has.Count.EqualTo(2));
		Assert.Multiple(() => {
			var wallA = decoded.Entities[0];
			Assert.That(wallA.SourcePath, Is.EqualTo("Map/WallA"));
			Assert.That(wallA.Type, Is.EqualTo(LevelEntityType.StaticGeometry));
			Assert.That(wallA.Transform.Position.X, Is.EqualTo(Fixed64.FP.FromRatio(-5, 1)));
			Assert.That(wallA.Components, Is.EqualTo(new PlacementComponents(
				Body: BodyType.Static,
				BoxShape: new BoxShapeData(new FVector3(40.ToFP(), 3.ToFP(), FP.One), FP.One),
				Navigation: NavContribution.ObstacleOnly
			)));
			Assert.That(decoded.Entities[1].Components.Navigation, Is.EqualTo(NavContribution.Excluded));
		});
	}

	[Test]
	public void CodecRejectsInvalidNavigationContribution() {
		var box = Box("Map/Wall", 0, (NavContribution)byte.MaxValue);

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([box])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Wall"));
	}

	[Test]
	public void LevelFileConnectionKeyDependsOnNavigationContribution() {
		var walkable = LevelDataCodec.Serialize(new LevelData([Box("Map/Ground", 0)]));
		var obstacle = LevelDataCodec.Serialize(new LevelData([Box("Map/Ground", 0, NavContribution.ObstacleOnly)]));

		Assert.That(LevelFile.Read("walkable", walkable).ConnectionKey,
			Is.Not.EqualTo(LevelFile.Read("obstacle", obstacle).ConnectionKey));
	}

	[Test]
	public void CodecRejectsOversizedStaticGeometry() {
		var box = Box("Map/Wall", 0) with {
			Components = Box("Map/Wall", 0).Components with {
				BoxShape = new BoxShapeData(new FVector3(41.ToFP(), FP.One, FP.One), FP.One)
			}
		};
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([box])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Wall"));
	}

	[Test]
	public void StaticGeometryRequiresNavigation() {
		var box = Box("Map/Wall", 0);
		box = box with { Components = box.Components with { Navigation = null } };

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([box])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Wall").And.Message.Contains("missing").And.Message.Contains("Navigation"));
	}

	[Test]
	public void CrateRejectsNavigation() {
		var crate = Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero);
		crate = crate with { Components = crate.Components with { Navigation = NavContribution.Walkable } };

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([crate])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Navigation not allowed on Crate"));
	}

	[TestCase(LevelEntityComponentKind.Health)]
	[TestCase(LevelEntityComponentKind.Loot)]
	[TestCase(LevelEntityComponentKind.View)]
	public void StaticGeometryRejectsCrateComponents(LevelEntityComponentKind kind) {
		var box = Box("Map/Wall", 0);
		var components = kind switch {
			LevelEntityComponentKind.Health => box.Components with { Health = 100 },
			LevelEntityComponentKind.Loot => box.Components with { Loot = LootKind.None },
			LevelEntityComponentKind.View => box.Components with { View = ViewAsset.Crate },
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([box with { Components = components }])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains($"{kind} not allowed on StaticGeometry"));
	}

	[Test]
	public void StaticGeometryRejectsDynamicBody() {
		var box = Box("Map/Wall", 0);
		box = box with { Components = box.Components with { Body = BodyType.Dynamic } };

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([box])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Wall").And.Message.Contains("Static body"));
	}

	[Test]
	public void CrateRejectsStaticBody() {
		var crate = Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero);
		crate = crate with { Components = crate.Components with { Body = BodyType.Static } };

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([crate])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Crate").And.Message.Contains("Dynamic or Kinematic body"));
	}

	[Test]
	public void CodecRejectsUnknownComponentMaskBits() {
		var bytes = LevelDataCodec.Serialize(new LevelData([Box("Map/Wall", 0)]));
		// Payload: entity count, source path (length-prefixed), type byte, transform (3 longs, 4 ints), mask.
		var maskOffset = LevelDataCodec.EnvelopeSize + sizeof(int) + 1 + "Map/Wall".Length + 1 + 3 * sizeof(long) + 4 * sizeof(int);
		Assert.That(BitConverter.ToUInt16(bytes, maskOffset), Is.EqualTo(0b10_1100), "mask of Body, BoxShape and Navigation");
		bytes[maskOffset + 1] |= 0x80;
		SHA256.HashData(bytes.AsSpan(LevelDataCodec.EnvelopeSize)).CopyTo(bytes, LevelDataCodec.HashOffset);

		Assert.That(() => LevelDataCodec.Deserialize(bytes),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("unknown entity components"));
	}

	[Test]
	public void CodecRoundTripsDoorsAndPlates() {
		var level = new LevelData([Door("Map/Door"), Plate("Map/Plate")], navZones: [GateZone()]);
		var bytes = LevelDataCodec.Serialize(level);
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.Multiple(() => {
			Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
			Assert.That(decoded.Entities[0].Components, Is.EqualTo(Door("Map/Door").Components));
			Assert.That(decoded.Entities[1].Components, Is.EqualTo(Plate("Map/Plate").Components));
			Assert.That(decoded.NavigationSources, Is.Empty, "doors and plates are not bake input");
		});
	}

	[Test]
	public void CodecRoundTripsRailPlatforms() {
		var platform = Platform("Map/Elevator");
		var level = new LevelData([platform], navZones: [GateZone()]);
		var bytes = LevelDataCodec.Serialize(level);
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.Multiple(() => {
			Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
			Assert.That(decoded.Entities.Single().Type, Is.EqualTo(LevelEntityType.Platform));
			Assert.That(decoded.Entities.Single().Components, Is.EqualTo(platform.Components));
			Assert.That(decoded.NavigationSources, Is.EqualTo(new[] {
				new NavSourceBox(platform.SourcePath, platform.Transform, platform.Components.BoxShape!.Value.HalfExtents, NavContribution.Walkable),
			}));
		});
	}

	[Test]
	public void ZoneLinksMustNameALevelZone() {
		Assert.Multiple(() => {
			Assert.That(() => LevelDataCodec.Serialize(new LevelData([Door("Map/Door")])),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Door").And.Message.Contains("names no navigation zone"));
			var unlinked = Door("Map/Door");
			unlinked = unlinked with { Components = unlinked.Components with { ZoneLink = null } };
			Assert.That(() => LevelDataCodec.Serialize(new LevelData([unlinked], navZones: [GateZone()])),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing").And.Message.Contains("ZoneLink"));
		});
	}

	[Test]
	public void OneZoneCannotMixDoorAndPlatformControllers() {
		var level = new LevelData([Door("Map/Door"), Platform("Map/Platform")], navZones: [GateZone()]);

		Assert.That(() => LevelDataCodec.Serialize(level),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("both doors and platforms"));
	}

	[Test]
	public void DoorsAndPlatesCheckTheirComponents() {
		var door = Door("Map/Door");
		var plate = Plate("Map/Plate");
		Assert.Multiple(() => {
			Assert.That(() => LevelDataValidation.Validate(door with { Components = door.Components with { Body = BodyType.Dynamic } }),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("Kinematic body"));
			Assert.That(() => LevelDataValidation.Validate(door with { Components = door.Components with { DoorMotion = new DoorMotionData(FVector3.Zero, FP.One, false) } }),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("open offset"));
			Assert.That(() => LevelDataValidation.Validate(plate with { Components = plate.Components with { View = ViewAsset.Door } }),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("View not allowed on PressurePlate"));
			Assert.That(() => LevelDataValidation.Validate(plate with { Components = plate.Components with { Body = BodyType.Kinematic } }),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("Static body"));
			Assert.That(() => LevelDataValidation.Validate(door with { Components = door.Components with { RailMotion = new RailMotionData(FVector3.Up, FP.One, false) } }),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("RailMotion not allowed on Door"));
		});
	}

	[Test]
	public void NavigationSourcesIncludeStaticGeometryAndPlatformStartPosesOrderedBySourcePath() {
		var level = new LevelData([
			Box("Map/WallB", 3, NavContribution.ObstacleOnly),
			Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero),
			Platform("Map/Platform"),
			Box("Map/WallA", -5),
		]);

		Assert.That(level.NavigationSources, Is.EqualTo(new[] {
			new NavSourceBox("Map/Platform", Platform("Map/Platform").Transform, new FVector3(FP.Two, FP.Quarter, FP.Two), NavContribution.Walkable),
			new NavSourceBox("Map/WallA", Box("Map/WallA", -5).Transform, new FVector3(40.ToFP(), 3.ToFP(), FP.One), NavContribution.Walkable),
			new NavSourceBox("Map/WallB", Box("Map/WallB", 3).Transform, new FVector3(40.ToFP(), 3.ToFP(), FP.One), NavContribution.ObstacleOnly),
		}));
	}

	[Test]
	public void CodecRejectsDynamicBoxWhoseMassExceedsPhysicsLimit() {
		var placement = Placement("Map/HeavyCrate", 100, LootKind.None, Fixed64.FP.Zero);
		placement = placement with {
			Components = placement.Components with { BoxShape = new BoxShapeData(new FVector3(4.ToFP(), 4.ToFP(), 4.ToFP()), FP.Two) }
		};

		Assert.That(() => LevelDataCodec.Serialize(new LevelData([placement])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/HeavyCrate"));
	}

	[Test]
	public void DynamicBoxWithinLimitsPassesExportAndRuntimeValidation() {
		var placement = Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero);
		placement = placement with {
			Components = placement.Components with { BoxShape = new BoxShapeData(new FVector3(FP.One, FP.One, FP.One), FP.Two) }
		};
		var shape = Shape.MakeBox(FVector3.Zero, placement.Components.BoxShape!.Value.HalfExtents);
		shape.Density = placement.Components.BoxShape.Value.Density;

		Assert.Multiple(() => {
			Assert.That(() => LevelDataCodec.Serialize(new LevelData([placement])), Throws.Nothing);
			Assert.That(() => PhysicsValidation.ValidateShape(shape, placement.Components.Body!.Value, placement.Transform, placement.SourcePath), Throws.Nothing);
		});
	}

	[TestCase(ShapeType.Sphere)]
	[TestCase(ShapeType.Capsule)]
	[TestCase(ShapeType.Hull)]
	public void RuntimeValidationAppliesMassLimitsToEverySupportedShape(ShapeType shapeType) {
		var shape = shapeType switch {
			ShapeType.Sphere => Shape.MakeSphere(FVector3.Zero, 4.ToFP()),
			ShapeType.Capsule => Shape.MakeCapsule(FVector3.Zero, FVector3.Zero, 4.ToFP()),
			ShapeType.Hull => Shape.MakeBox(FVector3.Zero, new FVector3(4.ToFP(), 4.ToFP(), 4.ToFP())),
			_ => throw new ArgumentOutOfRangeException(nameof(shapeType))
		};
		shape.Density = FP.Two;

		Assert.That(() => PhysicsValidation.ValidateShape(shape, BodyType.Dynamic, FWorldTransform.Identity, "shape"),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void LoaderCreatesStaticBodiesWithoutViews() {
		W.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<LevelTestWorld>();
		W.Initialize();
		try {
			LevelLoader.Load(new LevelData([Box("Map/WallA", 0), Box("Map/WallB", 20)]));
			var count = 0;
			foreach (var wall in W.Query<All<Body, Transform>>().Entities()) {
				count++;
				Assert.Multiple(() => {
					Assert.That(wall.Read<Body>().Type, Is.EqualTo(BodyType.Static));
					Assert.That(wall.Has<ViewId>(), Is.False);
					Assert.That(wall.Read<W.Links<Shapes>>().Length, Is.EqualTo(1));
				});
			}
			Assert.That(count, Is.EqualTo(2));
		} finally {
			W.Destroy();
		}
	}

	[Test]
	public void LevelFileConnectionKeyDependsOnContent() {
		var a = LevelDataCodec.Serialize(new LevelData([Placement("Map/A", 100, LootKind.None, Fixed64.FP.Zero)]));
		var b = LevelDataCodec.Serialize(new LevelData([Placement("Map/A", 101, LootKind.None, Fixed64.FP.Zero)]));

		Assert.That(LevelFile.Read("a", a).ConnectionKey, Is.EqualTo(LevelFile.Read("a2", a).ConnectionKey));
		Assert.That(LevelFile.Read("a", a).ConnectionKey, Is.Not.EqualTo(LevelFile.Read("b", b).ConnectionKey));
	}

	[Test]
	public void LevelFileRejectsTamperedBytesWithLevelName() {
		var bytes = LevelDataCodec.Serialize(new LevelData([Placement("Map/A", 100, LootKind.None, Fixed64.FP.Zero)]));
		bytes[^1] ^= 0x01;
		Assert.That(() => LevelFile.Read("arena", bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("arena"));
	}

	[Test]
	public void LevelFileNameStripsExtension() {
		Assert.That(LevelFile.NameFromPath("/srv/levels/arena.level.bytes"), Is.EqualTo("arena"));
	}

	[Test]
	public void ServerSetupCleansUpWhenRuntimeLevelValidationFails() {
		var validBytes = LevelDataCodec.Serialize(new LevelData([Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero)]));
		var level = LevelFile.Read("runtime-invalid", validBytes);
		var entities = (EntityPlacement[])level.Data.Entities;
		entities[0] = entities[0] with {
			Components = entities[0].Components with {
				BoxShape = new BoxShapeData(new FVector3(4.ToFP(), 4.ToFP(), 4.ToFP()), FP.Two)
			}
		};
		var listener = new TestRemoteClientListener();

		try {
			Assert.That(() => ServerSetup.CreateAndInitialize(listener, level), Throws.TypeOf<InvalidDataException>());
			Assert.Multiple(() => {
				Assert.That(SRVR.IsCreated, Is.False);
				Assert.That(World<ServerWorld>.Status, Is.EqualTo(WorldStatus.NotCreated));
				Assert.That(listener.IsListening, Is.False);
			});
		} finally {
			if (World<ServerWorld>.Status != WorldStatus.NotCreated) {
				Core<ServerWorld>.GameWorldSetup.Destroy();
			}
			if (SRVR.IsCreated) {
				SRVR.Destroy();
			}
		}
	}

	[Test]
	public void ExportedSampleContainsDifferentCrateHealthOverrides() {
		var path = PhysicsSmokeTest.TestLevels.SampleFile(TestContext.CurrentContext.TestDirectory);
		var level = LevelFile.ReadFromDisk(path);
		var defaultCrate = level.Data.Entities.Single(static entity => entity.SourcePath == "DefaultCrate");
		var strongCrate = level.Data.Entities.Single(static entity => entity.SourcePath == "StrongCrate");
		var ground = level.Data.Entities.Single(static entity => entity.SourcePath == "Ground");
		var testBox = level.Data.Entities.Single(static entity => entity.SourcePath == "TestBox");

		Assert.Multiple(() => {
			Assert.That(strongCrate.Components.Health, Is.Not.EqualTo(defaultCrate.Components.Health));
			Assert.That(ground.Type, Is.EqualTo(LevelEntityType.StaticGeometry));
			Assert.That(ground.Components.Navigation, Is.EqualTo(NavContribution.Walkable));
			Assert.That(testBox.Type, Is.EqualTo(LevelEntityType.StaticGeometry));
			Assert.That(testBox.Components.Navigation, Is.EqualTo(NavContribution.ObstacleOnly));
			Assert.That(level.Data.Entities.Single(static entity => entity.SourcePath == "ElevatorPlatform").Type, Is.EqualTo(LevelEntityType.Platform));
			Assert.That(level.Data.Entities.Single(static entity => entity.SourcePath == "ElevatorPlatform").Components.ZoneLink, Is.EqualTo("elevator"));
			Assert.That(level.Data.Entities.Single(static entity => entity.SourcePath == "ElevatorPlatform").Components.Navigation, Is.EqualTo(NavContribution.Walkable));
			Assert.That(level.Data.Entities.Where(static entity => entity.Type == LevelEntityType.StaticGeometry).Any(static entity =>
				entity.Transform.Position.X - entity.Components.BoxShape!.Value.HalfExtents.X.To64() < Fixed64.FP.FromRatio(-12, 1)
				&& entity.Transform.Position.X + entity.Components.BoxShape.Value.HalfExtents.X.To64() > Fixed64.FP.FromRatio(-12, 1)
				&& entity.Transform.Position.Z - entity.Components.BoxShape.Value.HalfExtents.Z.To64() < Fixed64.FP.FromRatio(32, 1)
				&& entity.Transform.Position.Z + entity.Components.BoxShape.Value.HalfExtents.Z.To64() > Fixed64.FP.FromRatio(32, 1)), Is.True,
				"continuous ground remains underneath the elevator");
			Assert.That(level.Data.Entities.Single(static entity => entity.SourcePath == "VaultDoor").Components.ZoneLink, Is.EqualTo("vault"));
			Assert.That(level.Data.Entities.Single(static entity => entity.SourcePath == "VaultPlate").Type, Is.EqualTo(LevelEntityType.PressurePlate));
		});
	}

	[Test]
	public void LoaderCreatesCrateBodyAndOwnedShape() {
		W.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<LevelTestWorld>();
		W.Initialize();
		try {
			LevelLoader.Load(new LevelData([Placement("Map/Crate", 250, LootKind.Ammo, Fixed64.FP.FromRatio(2, 1))]));
			var count = 0;
			foreach (var crate in W.Query<All<Health, LootDrop, Body, ViewId>>().Entities()) {
				count++;
				Assert.Multiple(() => {
					Assert.That(crate.Read<Health>().Value, Is.EqualTo(250));
					Assert.That(crate.Read<LootDrop>().Kind, Is.EqualTo(LootKind.Ammo));
					Assert.That(crate.Read<ViewId>().Value, Is.EqualTo(ViewAsset.Crate));
					Assert.That(crate.Read<Body>().Type, Is.EqualTo(BodyType.Dynamic));
					Assert.That(crate.Read<W.Links<Shapes>>().Length, Is.EqualTo(1));
				});
			}
			Assert.That(count, Is.EqualTo(1));
		} finally {
			W.Destroy();
		}
	}

	[Test]
	public void LoaderSpawnsStaticGeometryBeforeCrates() {
		W.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<LevelTestWorld>();
		W.Initialize();
		try {
			LevelLoader.Load(new LevelData([
				Placement("Map/Crate", 100, LootKind.None, Fixed64.FP.Zero),
				Box("Map/Wall", 20),
			]));
			var crateIds = new List<uint>();
			foreach (var crate in W.Query<All<ViewId>>().Entities()) {
				crateIds.Add(crate.ID);
			}
			var wallIds = new List<uint>();
			foreach (var wall in W.Query<All<Body>, None<ViewId>>().Entities()) {
				wallIds.Add(wall.ID);
			}

			Assert.That(crateIds, Has.Count.EqualTo(1));
			Assert.That(wallIds, Has.Count.EqualTo(1));
			Assert.That(wallIds[0], Is.LessThan(crateIds[0]));
		} finally {
			W.Destroy();
		}
	}

	private static NavZoneVolume GateZone() => new(
		"gate",
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(4, 1), Fixed64.FP.Half, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity),
		new FVector3(FP.Two, FP.One, 3.ToFP())
	);

	private static EntityPlacement Door(string path) => PhysicsSmokeTest.TestLevels.Door(
		path,
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(4, 1), Fixed64.FP.FromRatio(3, 2), Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity),
		new FVector3(FP.Two, FP.One, FP.Quarter),
		"gate",
		new FVector3(5.ToFP(), FP.Zero, FP.Zero),
		FP.Two,
		startsOpen: true
	);

	private static EntityPlacement Plate(string path) => PhysicsSmokeTest.TestLevels.PressurePlate(
		path,
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(-7, 1), Fixed64.FP.Half, Fixed64.FP.Zero), FQuaternion.Identity),
		new FVector3(FP.One, FP.Quarter, FP.One),
		"gate"
	);

	private static EntityPlacement Platform(string path) => PhysicsSmokeTest.TestLevels.Platform(
		path,
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(4, 1), Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity),
		new FVector3(FP.Two, FP.Quarter, FP.Two),
		"gate",
		new FVector3(FP.Zero, 4.ToFP(), FP.Zero),
		FP.One
	);

	private static EntityPlacement Box(string path, int x, NavContribution navigation = NavContribution.Walkable) => PhysicsSmokeTest.TestLevels.StaticBox(
		path,
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(x, 1), Fixed64.FP.FromRatio(3, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		new FVector3(40.ToFP(), 3.ToFP(), FP.One),
		navigation
	);

	private static EntityPlacement Placement(string path, int health, LootKind loot, Fixed64.FP x) => new(
		path,
		LevelEntityType.Crate,
		new FWorldTransform(new FPos(x, Fixed64.FP.Half, Fixed64.FP.Zero), FQuaternion.Identity),
		new PlacementComponents(
			Health: health,
			Loot: loot,
			Body: BodyType.Dynamic,
			BoxShape: new BoxShapeData(new FVector3(FP.Half, FP.Half, FP.Half), FP.One),
			View: ViewAsset.Crate
		)
	);

	private sealed class TestRemoteClientListener : IRemoteClientListener {
		public bool IsListening { get; private set; }
		public void Start() => IsListening = true;
		public void Stop() => IsListening = false;
		public void Poll() { }
		public bool TryAccept(out RemoteClientConnection connection) {
			connection = null!;
			return false;
		}
		public void Release(RemoteClientConnection connection) { }
	}
}
