using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using Shenanicode.Rollback;
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
			Assert.That(decoded.Entities[0].Crate.Health, Is.EqualTo(250));
			Assert.That(decoded.Entities[0].Crate.Loot, Is.EqualTo(LootKind.Ammo));
			Assert.That(decoded.Entities[0].Transform.Position.X, Is.EqualTo(a.Transform.Position.X));
			Assert.That(decoded.Entities[1].Crate.Health, Is.EqualTo(40));
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
	public void CodecRejectsOverlongSourcePathBeforeWriting() {
		var placement = Placement(new string('x', 1025), 100, LootKind.None, Fixed64.FP.Zero);
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([placement])), Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void CodecRoundTripsStaticBoxes() {
		var level = new LevelData([], [Box("Map/WallB", 3), Box("Map/WallA", -5)]);
		var bytes = LevelDataCodec.Serialize(level);
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
		Assert.That(decoded.StaticBoxes, Has.Count.EqualTo(2));
		Assert.Multiple(() => {
			Assert.That(decoded.StaticBoxes[0].SourcePath, Is.EqualTo("Map/WallA"));
			Assert.That(decoded.StaticBoxes[0].Transform.Position.X, Is.EqualTo(Fixed64.FP.FromRatio(-5, 1)));
			Assert.That(decoded.StaticBoxes[0].HalfExtents, Is.EqualTo(new FVector3(40.ToFP(), 3.ToFP(), FP.One)));
		});
	}

	[Test]
	public void CodecRejectsOversizedStaticBox() {
		var box = Box("Map/Wall", 0) with { HalfExtents = new FVector3(41.ToFP(), FP.One, FP.One) };
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([], [box])),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("Map/Wall"));
	}

	[Test]
	public void LoaderCreatesStaticBodiesWithoutViews() {
		W.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<LevelTestWorld>();
		W.Initialize();
		try {
			LevelLoader.Load(new LevelData([], [Box("Map/WallA", 0), Box("Map/WallB", 20)]));
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

	private static StaticBox Box(string path, int x) => new(
		path,
		new FWorldTransform(new FPos(Fixed64.FP.FromRatio(x, 1), Fixed64.FP.FromRatio(3, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		new FVector3(40.ToFP(), 3.ToFP(), FP.One)
	);

	private static EntityPlacement Placement(string path, int health, LootKind loot, Fixed64.FP x) => new(
		path,
		LevelEntityType.Crate,
		new FWorldTransform(new FPos(x, Fixed64.FP.Half, Fixed64.FP.Zero), FQuaternion.Identity),
		new CratePlacementData(
			health,
			loot,
			BodyType.Dynamic,
			new FVector3(FP.Half, FP.Half, FP.Half),
			FP.One,
			ViewAsset.Crate
		)
	);
}
