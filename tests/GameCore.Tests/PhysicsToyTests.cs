using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Core<Space.GameCore.Tests.PhysicsToyWorld>;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct PhysicsToyWorld : IWorldType, ISessionType;

[TestFixture, NonParallelizable]
public sealed class PhysicsToyTests {
	[TearDown]
	public void TearDown() => NavTestSession.DestroyIfCreated<PhysicsToyWorld>();

	[Test]
	public void AuthoredMaterialsAndSpheresRoundTripAndLoad() {
		var source = Sample();
		var bytes = LevelDataCodec.Serialize(source);
		var decoded = LevelDataCodec.Deserialize(bytes);
		Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
		Assert.That(decoded.Entities, Is.EqualTo(source.Entities));
		NavTestSession.CreateWorld<PhysicsToyWorld>(decoded);
		var spheres = 0;
		var conveyors = 0;
		var pads = 0;
		foreach (var entity in W.Query<All<Shape>>().Entities()) {
			ref readonly var shape = ref entity.Read<Shape>();
			if (shape.Type == ShapeType.Sphere)
				spheres++;
			if (shape.Material.TangentVelocity != FVector3.Zero)
				conveyors++;
			if (shape.CharacterBounceSpeed > FP.Zero)
				pads++;
		}
		Assert.Multiple(() => {
			Assert.That(spheres, Is.GreaterThanOrEqualTo(6));
			Assert.That(conveyors, Is.EqualTo(1));
			Assert.That(pads, Is.EqualTo(1));
		});
	}

	[TestCase(0)]
	[TestCase(90)]
	public void ConveyorCarriesCrateAndSphereInItsLocalDirection(int yaw) {
		var material = SurfaceMaterial.Default;
		material.Friction = FP.One;
		material.TangentVelocity = new FVector3(FP.Zero, FP.Zero, 3.ToFP());
		var rotation = new FQuaternion(FP.Zero, Math.Sin(yaw * Math.PI / 360).ToFP(), FP.Zero, Math.Cos(yaw * Math.PI / 360).ToFP());
		var floor = Floor(material) with { Transform = new FWorldTransform(FPos.Zero, rotation) };
		var crate = new EntityPlacement("Crate", LevelEntityType.Crate, Pose(-2, 1.1, 0), new PlacementComponents(
			Health: 100, Loot: LootKind.None, Body: BodyType.Dynamic,
			BoxShape: new BoxShapeData(new FVector3(FP.Half, FP.Half, FP.Half), FP.One), View: ViewAsset.Crate));
		var sphereMaterial = SurfaceMaterial.Default;
		sphereMaterial.RollingResistance = FP.FromRatio(1, 4);
		var sphere = Sphere("Sphere", 2, 1.1, 0, sphereMaterial);
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([floor, crate, sphere]));
		var crateGid = FindBody(-2, 1.1, 0);
		var sphereGid = FindBody(2, 1.1, 0);
		Step(120);
		Assert.Multiple(() => {
			Assert.That(Displacement(crateGid, crate.Transform.Position, rotation * FVector3.Forward), Is.GreaterThan(2));
			Assert.That(Displacement(sphereGid, sphere.Transform.Position, rotation * FVector3.Forward), Is.GreaterThan(1));
		});
	}

	[Test]
	public void DensePhysicsAreaRemainsStableAndFitsRollbackFrames() {
		var sample = Sample();
		var props = sample.Entities.Where(static entity => entity.SourcePath.StartsWith("PhysicsStress/", StringComparison.Ordinal)
			&& entity.Type is LevelEntityType.Crate or LevelEntityType.Sphere).ToArray();
		Assert.Multiple(() => {
			Assert.That(props.Count(static entity => entity.Type == LevelEntityType.Crate), Is.EqualTo(64));
			Assert.That(props.Count(static entity => entity.Type == LevelEntityType.Sphere), Is.EqualTo(32));
		});
		NavTestSession.CreateWorld<PhysicsToyWorld>(sample);
		var positions = props.Select(static prop => prop.Transform.Position).ToHashSet();
		var gids = new List<EntityGID>();
		foreach (var body in W.Query<All<Body>>().Entities()) {
			if (body.Read<Body>().Type == BodyType.Dynamic && positions.Contains(body.Read<Body>().Transform.Position)) {
				gids.Add(body.GID);
			}
		}
		Assert.That(gids, Has.Count.EqualTo(96));
		var playerGid = SpawnPlayer();
		Assert.That(playerGid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		player.Ref<Transform>().SetFromWorldTransform(Pose(9, 1.5, -33));
		var writer = NavTestSession.SnapshotWriter<PhysicsToyWorld>();
		var maximumSnapshotSize = 0;
		for (var tick = 0; tick < 480; tick++) {
			S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
			NavTestSession.Step<PhysicsToyWorld>();
			if (tick % 60 == 0) {
				writer.Position = 0;
				W.Serializer.CreateWorldSnapshot(ref writer);
				maximumSnapshotSize = Math.Max(maximumSnapshotSize, (int)writer.Position);
			}
		}
		foreach (var gid in gids) {
			Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var body), Is.True, "stress props survive the push and settling run");
			var position = body.Read<Body>().Transform.Position;
			Assert.That(PhysicsValidation.IsInsideSimulationBounds(position), Is.True);
			Assert.That(F.FConversions.ToDouble(position.Y), Is.GreaterThan(0.9), $"props do not fall through the continuous ground: {position}");
		}
		TestContext.WriteLine($"maximum many-object snapshot: {maximumSnapshotSize} / {writer.Buffer.Length} bytes");
		Assert.That(maximumSnapshotSize, Is.LessThan(writer.Buffer.Length));
	}

	[TestCase(1)]
	[TestCase(4)]
	[TestCase(6)]
	public void PushedStressClusterStaysAboveGround(int objectCount) {
		var sample = Sample();
		var ground = sample.Entities.Single(static prop => prop.SourcePath == "Ground");
		var props = sample.Entities.Where(static prop => prop.SourcePath.StartsWith("PhysicsStress/Cluster00/", StringComparison.Ordinal)).Take(objectCount).ToArray();
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([ground, .. props]));
		var gids = props.Select(prop => FindBody(prop.Transform.Position)).ToArray();
		var playerGid = SpawnPlayer();
		Assert.That(playerGid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		player.Ref<Transform>().SetFromWorldTransform(Pose(9, 1.5, -33));
		for (var tick = 0; tick < 480; tick++) {
			S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
			NavTestSession.Step<PhysicsToyWorld>();
		}
		foreach (var gid in gids) {
			Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var body), Is.True);
			Assert.That(F.FConversions.ToDouble(body.Read<Body>().Transform.Position.Y), Is.GreaterThan(0.9));
		}
	}

	[Test]
	public void ConveyorCarriesAnIdleCharacter() {
		var material = SurfaceMaterial.Default;
		material.TangentVelocity = new FVector3(FP.Zero, FP.Zero, 3.ToFP());
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([Floor(material)]));
		var playerGid = SpawnPlayer();
		Step(60);
		Assert.That(playerGid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		Assert.Multiple(() => {
			Assert.That(F.FConversions.ToDouble(player.Read<Transform>().Position.Z), Is.GreaterThan(2.8));
			Assert.That(player.Read<Mover>().Grounded, Is.True);
		});
	}

	[Test]
	public void BouncePadReboundsRigidPropsAndLaunchesCharacterWithoutRetriggeringOnAscent() {
		var material = SurfaceMaterial.Default;
		material.Restitution = FP.FromRatio(95, 100);
		var pad = Floor(material, 14.ToFP());
		var crate = new EntityPlacement("Crate", LevelEntityType.Crate, Pose(-3, 5, 0), new PlacementComponents(
			Health: 100, Loot: LootKind.None, Body: BodyType.Dynamic,
			BoxShape: new BoxShapeData(new FVector3(FP.Half, FP.Half, FP.Half), FP.One), View: ViewAsset.Crate));
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([pad, crate, Sphere("Sphere", 3, 5, 0, SurfaceMaterial.Default)]));
		var crateGid = FindBody(-3, 5, 0);
		var sphereGid = FindBody(3, 5, 0);
		var playerGid = SpawnPlayer();
		Step(2);
		Assert.That(playerGid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		Assert.That(player.Read<Mover>().Velocity.Y, Is.EqualTo(14.ToFP()));
		Step(30);
		Assert.That(playerGid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
		Assert.Multiple(() => {
			Assert.That(player.Read<Mover>().Velocity.Y.ToDouble(), Is.InRange(1, 3));
			Assert.That(player.Read<Mover>().Grounded, Is.False);
			Assert.That(F.FConversions.ToDouble(player.Read<Transform>().Position.Y), Is.GreaterThan(5));
		});
		var crateBounce = false;
		var sphereBounce = false;
		for (var i = 0; i < 120; i++) {
			Step(1);
			Assert.That(crateGid.TryUnpack<PhysicsToyWorld>(out var crateEntity), Is.True);
			Assert.That(sphereGid.TryUnpack<PhysicsToyWorld>(out var sphereEntity), Is.True);
			crateBounce |= crateEntity.Read<Body>().LinearVelocity.Y > 4.ToFP();
			sphereBounce |= sphereEntity.Read<Body>().LinearVelocity.Y > 4.ToFP();
		}
		Assert.Multiple(() => {
			Assert.That(crateBounce, Is.True);
			Assert.That(sphereBounce, Is.True);
		});
	}

	[TestCase(0)]
	[TestCase(0.25)]
	public void IcePreservesSlidingSpeedEvenWithRollingResistance(double resistance) {
		var ice = SurfaceMaterial.Default;
		ice.Friction = FP.Zero;
		var material = SurfaceMaterial.Default;
		material.RollingResistance = resistance.ToFP();
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([Floor(ice), Sphere("Sphere", 0, 1.1, 0, material)]));
		var gid = FindBody(0, 1.1, 0);
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var sphere), Is.True);
		sphere.Ref<Body>().LinearVelocity = new FVector3(FP.Zero, FP.Zero, FP.Two);
		sphere.Ref<Body>().AngularVelocity = new FVector3(4.ToFP(), FP.Zero, FP.Zero);
		sphere.Ref<Body>().AngularDamping = FP.Zero;
		Step(120);
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out sphere), Is.True);
		Assert.That(sphere.Read<Body>().LinearVelocity.Z.ToDouble(), Is.InRange(1.95, 2.05));
		Assert.That(Math.Abs(sphere.Read<Body>().AngularVelocity.X.ToDouble()), resistance == 0 ? Is.GreaterThan(3.5) : Is.LessThan(0.1));
	}

	[Test]
	public void BouncePadDoesNotLaunchACharacterUntilItsFeetReachTheSurface() {
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([Floor(SurfaceMaterial.Default, 14.ToFP())]));
		Step(1); // Install broad-phase proxies before placing the character.
		var gid = SpawnPlayer();
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.Two, F.FP.Zero);
		Step(1);
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
		Assert.That(player.Read<Mover>().Velocity.Y, Is.LessThan(FP.Zero), "a nearby pad is not contact");
		var launched = false;
		for (var tick = 0; tick < 60 && !launched; tick++) {
			Step(1);
			Assert.That(gid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
			launched = player.Read<Mover>().Velocity.Y == 14.ToFP();
		}
		Assert.That(launched, Is.True, "landing eventually triggers the pad");
	}

	[TestCase(20, true)]
	[TestCase(44, true)]
	[TestCase(46, false)]
	[TestCase(60, false)]
	public void GalleryGroundingAndBakedSurfaceAgreeEitherSideOfCutoff(int angle, bool walkable) {
		var sample = Sample();
		var ramp = sample.Entities.Single(e => e.SourcePath.EndsWith($"/Slope{angle}", StringComparison.Ordinal));
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([ramp]));
		Step(1);
		var center = ramp.Transform.Position + ramp.Transform.Rotation * new FVector3(FP.Zero, FP.FromRatio(1, 8), FP.Zero);
		var pogo = FP.Zero;
		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var grounded = CharacterMover.UpdatePogoGrounding(W.GetResource<BroadPhase>(),
			new FWorldTransform(center + FVector3.Up, FQuaternion.Identity), capsule, Const.DeltaTime.To32(),
			4.ToFP(), FP.FromRatio(7, 10), FP.Zero, new CharacterRes().MaxSlopeNormalThreshold.To32(), ref pogo);
		var nav = sample.Navigation!.Mesh;
		var hasElevatedSurface = false;
		foreach (var triangle in nav.Triangles) {
			var centroid = (nav.Vertices[triangle.V0] + nav.Vertices[triangle.V1] + nav.Vertices[triangle.V2]) / 3;
			if (Math.Abs(F.FConversions.ToDouble(centroid.X - center.X)) < 1.1 && Math.Abs(F.FConversions.ToDouble(centroid.Z - center.Z)) < 1.5 && F.FConversions.ToDouble(centroid.Y) > 1.2) {
				hasElevatedSurface = true;
			}
		}
		Assert.Multiple(() => {
			Assert.That(grounded, Is.EqualTo(walkable), "pogo grounding");
			Assert.That(hasElevatedSurface, Is.EqualTo(walkable), "baked ramp surface, excluding the floor underneath");
		});
	}

	[Test]
	public void InvalidMaterialsAreRejectedBeforeExport() {
		var material = SurfaceMaterial.Default;
		material.Friction = -FP.One;
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([Floor(material)])), Throws.TypeOf<InvalidDataException>());
		material.Friction = FP.One;
		material.TangentVelocity = new FVector3(61.ToFP(), FP.Zero, FP.Zero);
		Assert.That(() => LevelDataCodec.Serialize(new LevelData([Floor(material)])), Throws.TypeOf<InvalidDataException>());
	}

	[TestCase(20, true)]
	[TestCase(30, true)]
	[TestCase(40, true)]
	[TestCase(44, true)]
	[TestCase(45, true)]
	[TestCase(46, false)]
	[TestCase(50, false)]
	[TestCase(60, false)]
	[TestCase(50, true, 60)]
	[TestCase(40, false, 30)]
	public void WalkingUphillRespectsTheCharacterSlopeLimit(int angle, bool climbable, int slopeLimit = 45) {
		var ramp = Sample().Entities.Single(e => e.SourcePath.EndsWith($"/Slope{angle}", StringComparison.Ordinal));
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([ramp]));
		if (slopeLimit != 45) {
			Systems.GetResource<CharacterRes>().MaxSlopeNormalThreshold = F.FConversions.ToFP(Math.Cos(slopeLimit * Math.PI / 180));
		}
		Step(1);
		var gid = SpawnPlayer();
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		var normal = ramp.Transform.Rotation * FVector3.Up;
		var surface = ramp.Transform.Position + ramp.Transform.Rotation * new FVector3(FP.Zero, FP.FromRatio(1, 8), FP.Two);
		var start = surface + (FP.Half + FP.Half / normal.Y) * FVector3.Up;
		player.Ref<Transform>().SetFromWorldTransform(new FWorldTransform(start, FQuaternion.Identity));
		var highest = start.Y;
		for (var tick = 0; tick < 60; tick++) {
			S.SetApprovedInput(1, new PlayerInput { MoveY = -F.FP.One });
			NavTestSession.Step<PhysicsToyWorld>();
			Assert.That(gid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
			highest = F.FP.Max(highest, player.Read<Transform>().ToWorldTransform().Position.Y);
		}
		var rise = F.FConversions.ToDouble(highest - start.Y);
		Assert.That(rise, climbable ? Is.GreaterThan(0.5) : Is.LessThan(0.08), "uphill input must not create lift on a non-standable slope");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void SteepRampStillAllowsDownhillAndSidewaysMovement(bool sideways) {
		var ramp = Sample().Entities.Single(e => e.SourcePath.EndsWith("/Slope60", StringComparison.Ordinal));
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([ramp]));
		Step(1);
		var gid = SpawnPlayer();
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		var surface = ramp.Transform.Position + ramp.Transform.Rotation * new FVector3(FP.Zero, FP.FromRatio(1, 8), FP.Zero);
		var start = surface + FP.FromRatio(3, 2) * FVector3.Up;
		player.Ref<Transform>().SetFromWorldTransform(new FWorldTransform(start, FQuaternion.Identity));
		for (var tick = 0; tick < 30; tick++) {
			S.SetApprovedInput(1, new PlayerInput { MoveX = sideways ? F.FP.One : F.FP.Zero, MoveY = sideways ? F.FP.Zero : F.FP.One });
			NavTestSession.Step<PhysicsToyWorld>();
		}
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
		var displacement = player.Read<Transform>().ToWorldTransform().Position - start;
		Assert.That(sideways ? displacement.X.ToDouble() : displacement.Z.ToDouble(), Is.GreaterThan(1));
		Assert.That(displacement.Y, Is.LessThan(FP.Zero), "gravity still acts on non-standable slopes");
	}

	[Test]
	public void SteepRampDoesNotCancelAnExistingJumpImpulse() {
		var ramp = Sample().Entities.Single(e => e.SourcePath.EndsWith("/Slope60", StringComparison.Ordinal));
		NavTestSession.CreateWorld<PhysicsToyWorld>(new LevelData([ramp]));
		Step(1);
		var gid = SpawnPlayer();
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		var surface = ramp.Transform.Position + ramp.Transform.Rotation * new FVector3(FP.Zero, FP.FromRatio(1, 8), FP.Zero);
		var start = surface + FP.FromRatio(3, 2) * FVector3.Up;
		player.Ref<Transform>().SetFromWorldTransform(new FWorldTransform(start, FQuaternion.Identity));
		player.Ref<Mover>().Velocity.Y = 10.ToFP();
		player.Ref<Mover>().JumpCooldown = FP.FromRatio(1, 5);
		Step(15);
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out player), Is.True);
		Assert.That(F.FConversions.ToDouble(player.Read<Transform>().ToWorldTransform().Position.Y - start.Y), Is.GreaterThan(1.5));
	}

	private static EntityPlacement Floor(SurfaceMaterial material, FP bounce = default) => new("Floor", LevelEntityType.StaticGeometry, FWorldTransform.Identity,
		new PlacementComponents(Body: BodyType.Static, BoxShape: new BoxShapeData(new FVector3(12.ToFP(), FP.Half, 12.ToFP()), FP.One),
			Navigation: NavContribution.Walkable, SurfaceProperties: new SurfacePropertiesData(material, bounce)));

	private static EntityPlacement Sphere(string path, double x, double y, double z, SurfaceMaterial material) => new(path, LevelEntityType.Sphere, Pose(x, y, z),
		new PlacementComponents(Body: BodyType.Dynamic, View: ViewAsset.Sphere, SphereShape: new SphereShapeData(FP.Half, FP.One),
			SurfaceProperties: new SurfacePropertiesData(material, FP.Zero)));

	private static FWorldTransform Pose(double x, double y, double z) => new(new FPos(x.ToFP().To64(), y.ToFP().To64(), z.ToFP().To64()), FQuaternion.Identity);

	private static EntityGID FindBody(double x, double y, double z) => FindBody(Pose(x, y, z).Position);

	private static EntityGID FindBody(FPos position) {
		foreach (var entity in W.Query<All<Body>>().Entities()) {
			if (entity.Read<Body>().Transform.Position == position)
				return entity.GID;
		}
		throw new AssertionException("body not found");
	}

	private static EntityGID SpawnPlayer() {
		var player = W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 1 });
		player.Ref<Transform>().Position = new F.FVector3(F.FP.Zero, F.FP.FromRatio(3, 2), F.FP.Zero);
		return player.GID;
	}

	private static double Displacement(EntityGID gid, FPos origin, FVector3 direction) {
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var entity), Is.True);
		return FVector3.Dot(entity.Read<Body>().Transform.Position - origin, direction).ToDouble();
	}

	private static void Step(int ticks) {
		for (var i = 0; i < ticks; i++) {
			S.SetApprovedInput(1, new PlayerInput());
			NavTestSession.Step<PhysicsToyWorld>();
		}
	}

	private static LevelData Sample() {
		return LevelFile.ReadFromDisk(PhysicsSmokeTest.TestLevels.SampleFile(TestContext.CurrentContext.TestDirectory)).Data;
	}
}
