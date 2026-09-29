using Fixed;
using Fixed32;
using NUnit.Framework;
using Space.NavBuilder;
using F64 = Fixed64;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavMeshBakerTests {
	private static NavMeshBakeResult? s_sample;

	/// <summary>
	/// Recast rounds span tops up and lifts detail vertices by a voxel, so the baked surface sits up
	/// to two voxel heights above the collider it came from.
	/// </summary>
	private static readonly F64.FP MaxSurfaceLift = NavBakeSettings.Default.VoxelHeight * 2 + F64.FP.FromRatio(1, 1000);

	/// <summary>The exported sample level: an 80x80 walkable ground and a 4x1x4 obstacle-only box at (0, 1, -6).</summary>
	private static NavMeshBakeResult Sample() {
		return s_sample ??= NavMeshBaker.Bake(LoadSampleBoxes(), NavBakeSettings.Default);
	}

	[Test]
	public void SampleGroundIsWalkableAtGroundHeight() {
		var mesh = Sample().Mesh;
		var query = new NavMeshQuery(mesh);

		var triangle = query.FindTriangle(XZ(10, 10));

		Assert.That(triangle, Is.GreaterThanOrEqualTo(0));
		Assert.That(query.SampleHeight(XZ(10, 10), triangle), Is.InRange(F64.FP.Half, F64.FP.Half + MaxSurfaceLift));
	}

	[Test]
	public void SampleBoxFootprintIsBlockedAndItsTopIsNotWalkable() {
		var mesh = Sample().Mesh;
		var query = new NavMeshQuery(mesh);

		Assert.Multiple(() => {
			Assert.That(query.FindTriangle(XZ(0, -6)), Is.EqualTo(-1), "box centre");
			Assert.That(query.FindTriangle(new F64.FVector2(F64.FP.Zero, F64.FP.FromRatio(-39, 10))), Is.EqualTo(-1), "0.1 from the box side");
			Assert.That(query.FindTriangle(XZ(0, -3)), Is.GreaterThanOrEqualTo(0), "1.0 from the box side");
			Assert.That(mesh.Vertices.ToArray().Max(static v => v.Y), Is.LessThanOrEqualTo(F64.FP.Half + MaxSurfaceLift), "nothing is walkable above the ground");
		});
	}

	[Test]
	public void SamplePathBendsAroundTheBox() {
		var mesh = Sample().Mesh;
		var start = V(0, 0.5, -11);
		var end = V(0, 0.5, -1);
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		var corridor = new int[128];

		var status = pathfinder.FindPath(start, end, 1, corridor, out var length);
		var waypoints = new F64.FVector3[16];
		var count = new NavFunnel(mesh, NavConfig.Default).FindPath(corridor.AsSpan(0, length), start, end, waypoints);

		Assert.That(status, Is.EqualTo(NavPathStatus.Found));
		Assert.That(waypoints[..count].Any(static w => F64.FP.Abs(w.X) >= P(2)), Is.True, "a corner clears the box's x extent");
	}

	[Test]
	public void AgentRadiusSetsTheClearanceFromObstacles() {
		var wide = NavMeshBaker.Bake(LoadSampleBoxes(), NavBakeSettings.Default with { AgentRadius = F64.FP.FromRatio(3, 2) }).Mesh;

		Assert.Multiple(() => {
			Assert.That(new NavMeshQuery(Sample().Mesh).FindTriangle(XZ(0, -3)), Is.GreaterThanOrEqualTo(0));
			Assert.That(new NavMeshQuery(wide).FindTriangle(XZ(0, -3)), Is.EqualTo(-1));
		});
	}

	[Test]
	public void AgentHeightDecidesWhetherTheSpaceUnderASlabIsWalkable() {
		// The slab's underside is 1.5 above the ground.
		NavSourceBox[] boxes = [
			Ground(),
			Box("Slab", new FPos(P(0), P(9, 4), P(0)), new FVector3(3.ToFP(), FP.Quarter, 3.ToFP()), NavContribution.ObstacleOnly),
		];

		var tall = NavMeshBaker.Bake(boxes, NavBakeSettings.Default).Mesh;
		var short_ = NavMeshBaker.Bake(boxes, NavBakeSettings.Default with { AgentHeight = F64.FP.One }).Mesh;

		Assert.Multiple(() => {
			Assert.That(new NavMeshQuery(tall).FindTriangle(XZ(0, 0)), Is.EqualTo(-1));
			Assert.That(new NavMeshQuery(short_).FindTriangle(XZ(0, 0)), Is.GreaterThanOrEqualTo(0));
		});
	}

	[Test]
	public void AgentMaxClimbDecidesWhetherAStepIsConnected() {
		// A walkable 6x6 step whose top is 0.3 above the ground: three voxels, against a climb of
		// four voxels and of one.
		NavSourceBox[] boxes = [
			Ground(),
			Box("Step", new FPos(P(0), P(2, 5), P(0)), new FVector3(3.ToFP(), FP.FromRatio(2, 5), 3.ToFP()), NavContribution.Walkable),
		];

		var climbing = NavMeshBaker.Bake(boxes, NavBakeSettings.Default).Mesh;
		var stuck = NavMeshBaker.Bake(boxes, NavBakeSettings.Default with { AgentMaxClimb = F64.FP.FromRatio(1, 10) }).Mesh;

		Assert.Multiple(() => {
			Assert.That(FindPath(climbing, V(-8, 0.5, 0), V(0, 0.8, 0)), Is.EqualTo(NavPathStatus.Found));
			Assert.That(FindPath(stuck, V(-8, 0.5, 0), V(0, 0.8, 0)), Is.EqualTo(NavPathStatus.NoRoute));
		});
	}

	[Test]
	public void AgentMaxSlopeDecidesWhetherARampIsWalkable() {
		// A 30 degree ramp floating well above the ground, beside a flat pad.
		NavSourceBox[] boxes = [
			Box("Pad", new FPos(P(20), P(0), P(0)), new FVector3(3.ToFP(), FP.Half, 3.ToFP()), NavContribution.Walkable),
			Box("Ramp", new FPos(P(0), P(5), P(0)), new FVector3(3.ToFP(), FP.Quarter, 3.ToFP()), NavContribution.Walkable,
				FQuaternion.AxisAngleDegrees(FVector3.Right, 30.ToFP())),
		];

		var gentle = NavMeshBaker.Bake(boxes, NavBakeSettings.Default);
		var strict = NavMeshBaker.Bake(boxes, NavBakeSettings.Default with { AgentMaxSlopeDegrees = P(20) });

		Assert.Multiple(() => {
			Assert.That(new NavMeshQuery(gentle.Mesh).FindTriangle(XZ(0, 0)), Is.GreaterThanOrEqualTo(0));
			Assert.That(new NavMeshQuery(strict.Mesh).FindTriangle(XZ(0, 0)), Is.EqualTo(-1));
			Assert.That(strict.Report.WalkableCandidates, Is.LessThan(gentle.Report.WalkableCandidates));
		});
	}

	[Test]
	public void RepeatedBakesAreByteIdentical() {
		var boxes = LoadSampleBoxes();

		var first = NavMeshBaker.Bake(boxes, NavBakeSettings.Default);
		var second = NavMeshBaker.Bake(boxes.Reverse().ToArray(), NavBakeSettings.Default);

		Assert.That(NavMeshBytes.Of(second.Mesh), Is.EqualTo(NavMeshBytes.Of(first.Mesh)));
		Assert.That(second.Report, Is.EqualTo(first.Report));
	}

	[Test]
	public void BakedMeshIsOnTheSnapGridAndInTheWalkableArea() {
		var mesh = Sample().Mesh;

		Assert.Multiple(() => {
			Assert.That(mesh.Vertices.ToArray().All(static v => NavSnapGrid.IsOnGrid(v.X) && NavSnapGrid.IsOnGrid(v.Z)), Is.True);
			Assert.That(mesh.Areas.ToArray().All(static a => a.AreaMask == 1 << NavMeshBaker.WalkableArea), Is.True);
			Assert.That(Sample().Report.Build.NonManifoldEdges, Is.Zero);
		});
	}

	[Test]
	public void BakeWithoutWalkableGeometryFails() {
		NavSourceBox[] obstacles = [Box("Wall", new FPos(P(0), P(0), P(0)), new FVector3(3.ToFP(), FP.Half, 3.ToFP()), NavContribution.ObstacleOnly)];

		Assert.That(() => NavMeshBaker.Bake(obstacles, NavBakeSettings.Default), Throws.InstanceOf<InvalidDataException>());
	}

	[Test]
	public void BakeRejectsSourceOutsideTheRuntimeDomainBeforeRasterization() {
		var ground = Box("FarGround", new FPos(P(70), P(0), P(0)), new FVector3(2.ToFP(), FP.Half, 2.ToFP()), NavContribution.Walkable);

		Assert.That(() => NavMeshBaker.Bake([ground], NavBakeSettings.Default),
			Throws.InstanceOf<InvalidDataException>().With.Message.Contains("source vertex"));
	}

	[Test]
	public void BakeRejectsAnOversizedHeightfieldBeforeAllocation() {
		var settings = NavBakeSettings.Default with { VoxelSize = F64.FP.FromRatio(1, 4096) };

		Assert.That(() => NavMeshBaker.Bake([Ground()], settings),
			Throws.InstanceOf<InvalidDataException>().With.Message.Contains("heightfield"));
	}

	[Test]
	public void LevelWithoutWalkableGeometryRejectsZones() {
		var zone = Zone("gate", new FPos(P(0), P(0), P(0)));
		var level = new LevelData([], navZones: [zone]);

		Assert.That(() => NavMeshBaker.BakeLevel(level, NavBakeSettings.Default, out _),
			Throws.InstanceOf<InvalidDataException>().With.Message.Contains("covers no walkable navmesh"));
	}

	[Test]
	public void SampleZoneSplitsTheMeshAlongItsVolume() {
		var settings = NavBakeSettings.Default;
		var result = NavMeshBaker.Bake(LoadSampleBoxes(), LoadSampleZones(), settings);
		var mesh = result.Mesh;
		var zone = result.Zones.Single();
		var inZone = zone.Triangles.ToArray().ToHashSet();
		// Recast marks voxels by their centers and then simplifies region contours by up to
		// EdgeMaxError voxels, so the split follows the volume only within this tolerance.
		var tolerance = F64.FConversions.ToDouble((settings.EdgeMaxError + F64.FP.One) * settings.VoxelSize);
		var zoneArea = 0.0;

		Assert.Multiple(() => {
			Assert.That(zone.Id, Is.EqualTo("gate"));
			for (var t = 0; t < mesh.TriangleCount; t++) {
				ref readonly var triangle = ref mesh.Triangles[t];
				// The gate volume covers x in [2, 6], z in [-9, -3].
				var x = F64.FConversions.ToDouble(triangle.CenterXZ.X);
				var z = F64.FConversions.ToDouble(triangle.CenterXZ.Y);
				if (inZone.Contains(t)) {
					Assert.That(x > 2 - tolerance && x < 6 + tolerance && z > -9 - tolerance && z < -3 + tolerance, Is.True, $"zone triangle {t} at ({x}, {z}) lies outside the volume");
					zoneArea += Math.Abs(F64.FConversions.ToDouble(NavGeometry.SignedArea(mesh.GetVertexXZ(triangle.V0), mesh.GetVertexXZ(triangle.V1), mesh.GetVertexXZ(triangle.V2))));
				} else {
					Assert.That(x > 2 + tolerance && x < 6 - tolerance && z > -9 + tolerance && z < -3 - tolerance, Is.False, $"triangle {t} at ({x}, {z}) lies inside the volume but not in the zone");
				}
			}
			// Walkable ground inside the volume: x from the box's eroded edge at 2.5 to 6.
			Assert.That(zoneArea, Is.EqualTo(3.5 * 6).Within(1.5), "the zone covers the walkable ground inside its volume");
		});
	}

	[Test]
	public void ZonedBakesAreByteIdenticalWhateverTheInputOrder() {
		NavSourceBox[] boxes = [Ground()];
		NavZoneVolume[] zones = [
			Zone("b", new FPos(P(5), P(1, 2), P(0)), FQuaternion.AxisAngleDegrees(FVector3.Up, 30.ToFP())),
			Zone("a", new FPos(P(-5), P(1, 2), P(0))),
		];

		var first = NavMeshBaker.Bake(boxes, zones, NavBakeSettings.Default);
		var second = NavMeshBaker.Bake(boxes, zones.Reverse().ToArray(), NavBakeSettings.Default);

		Assert.Multiple(() => {
			Assert.That(NavMeshBytes.Of(second.Mesh), Is.EqualTo(NavMeshBytes.Of(first.Mesh)));
			Assert.That(first.Zones.Select(static zone => zone.Id), Is.EqualTo(new[] { "a", "b" }));
			for (var z = 0; z < 2; z++) {
				Assert.That(second.Zones[z].Triangles.ToArray(), Is.EqualTo(first.Zones[z].Triangles.ToArray()), $"zone {first.Zones[z].Id}");
				Assert.That(first.Zones[z].Triangles.Length, Is.GreaterThan(0));
			}
		});
	}

	[Test]
	public void InvalidZonesFailTheBake() {
		NavSourceBox[] boxes = [Ground()];
		var floating = Zone("floating", new FPos(P(0), P(10), P(0)));
		var many = Enumerable.Range(0, NavZoneData.MaxZones + 1).Select(i => Zone($"z{i:D2}", new FPos(P(0), P(1, 2), P(0)))).ToArray();

		Assert.Multiple(() => {
			Assert.That(() => NavMeshBaker.Bake(boxes, [floating], NavBakeSettings.Default),
				Throws.InstanceOf<InvalidDataException>().With.Message.Contains("covers no walkable navmesh"));
			Assert.That(() => NavMeshBaker.Bake(boxes, [Zone("a", new FPos(P(-5), P(0), P(0))), Zone("a", new FPos(P(5), P(0), P(0)))], NavBakeSettings.Default),
				Throws.InstanceOf<InvalidDataException>().With.Message.Contains("duplicated"));
			Assert.That(() => NavMeshBaker.Bake(boxes, many, NavBakeSettings.Default), Throws.InstanceOf<InvalidDataException>());
		});
	}

	[Test]
	public void NavigationZonesRejectPitchAndRoll() {
		var pitched = Zone("pitched", new FPos(P(0), P(1, 2), P(0)), FQuaternion.AxisAngleDegrees(FVector3.Right, 15.ToFP()));
		var yawed = Zone("yawed", new FPos(P(0), P(1, 2), P(0)), FQuaternion.AxisAngleDegrees(FVector3.Up, 30.ToFP()));

		Assert.Multiple(() => {
			Assert.That(() => pitched.Validate(), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Y axis"));
			Assert.That(() => yawed.Validate(), Throws.Nothing);
		});
	}

	[Test]
	public void RuntimeAssemblyDoesNotReferenceDotRecast() {
		var references = typeof(NavMesh).Assembly.GetReferencedAssemblies().Select(static a => a.Name);

		Assert.That(references, Has.None.StartsWith("DotRecast"));
	}

	private static NavPathStatus FindPath(NavMesh mesh, F64.FVector3 start, F64.FVector3 end) {
		var pathfinder = new NavPathfinder(new NavMeshQuery(mesh), NavConfig.Default);
		return pathfinder.FindPath(start, end, 1, new int[128], out _);
	}

	private static NavSourceBox Ground() {
		return Box("Ground", new FPos(P(0), P(0), P(0)), new FVector3(10.ToFP(), FP.Half, 10.ToFP()), NavContribution.Walkable);
	}

	private static NavSourceBox Box(string path, FPos position, FVector3 halfExtents, NavContribution navigation, FQuaternion? rotation = null) {
		return new NavSourceBox(path, new FWorldTransform(position, rotation ?? FQuaternion.Identity), halfExtents, navigation);
	}

	/// <summary>A 4x2x4 zone volume.</summary>
	private static NavZoneVolume Zone(string id, FPos position, FQuaternion? rotation = null) {
		return new NavZoneVolume(id, new FWorldTransform(position, rotation ?? FQuaternion.Identity), new FVector3(2.ToFP(), FP.One, 2.ToFP()));
	}

	private static NavZoneVolume[] LoadSampleZones() {
		return LevelFile.ReadFromDisk(FindRepositoryFile("Client", "maps", "level_pipeline_test.level.bytes")).Data.NavZones.ToArray();
	}

	private static NavSourceBox[] LoadSampleBoxes() {
		return LevelFile.ReadFromDisk(FindRepositoryFile("Client", "maps", "level_pipeline_test.level.bytes")).Data.NavigationSources.ToArray();
	}

	private static F64.FP P(int numerator, int denominator = 1) {
		return F64.FP.FromRatio(numerator, denominator);
	}

	private static F64.FVector2 XZ(int x, int z) {
		return new F64.FVector2(F64.FConversions.ToFP(x), F64.FConversions.ToFP(z));
	}

	private static F64.FVector3 V(double x, double y, double z) {
		return new F64.FVector3(F64.FConversions.ToFP(x), F64.FConversions.ToFP(y), F64.FConversions.ToFP(z));
	}

	private static string FindRepositoryFile(params string[] relativePath) {
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent) {
			var candidate = Path.Combine([directory.FullName, .. relativePath]);
			if (File.Exists(candidate)) {
				return candidate;
			}
		}
		throw new FileNotFoundException($"Could not find repository file '{Path.Combine(relativePath)}'.");
	}
}
