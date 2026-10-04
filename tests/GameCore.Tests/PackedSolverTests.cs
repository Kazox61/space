using FFS.Libraries.StaticEcs;
using NUnit.Framework;
using Shenanicode.Rollback;
using C = Space.GameCore.Core<Space.GameCore.Tests.PackedSolverWorld>;
using F = Fixed64;

namespace Space.GameCore.Tests;

public struct PackedSolverWorld : IWorldType, ISessionType;
public struct PackedSolverRestoreWorld : IWorldType, ISessionType;

[TestFixture, NonParallelizable]
public sealed class PackedSolverTests {
	// Recorded from the committed pre-D ECS solver (665585a), before changing production code.
	private const string PreDHashes =
		"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";

	[TearDown]
	public void TearDown() {
		NavTestSession.DestroyIfCreated<PackedSolverWorld>();
		NavTestSession.DestroyIfCreated<PackedSolverRestoreWorld>();
	}

	[Test]
	public void CachedDeltaRotationMatchesQuaternionOperatorRawResults() {
		var random = new Random(734209);
		for (var i = 0; i < 4096; i++) {
			Fixed32.FP Next(int bound) => Fixed32.FP.FromRaw(random.Next(-bound, bound + 1));
			// Include non-unit and negative-W values: caching must not normalize or alter
			// coefficients, even for static participants whose actual deltas are gathered.
			var q = new Fixed32.FQuaternion(Next(65536), Next(65536), Next(65536), Next(65536));
			var v = new Fixed32.FVector3(Next(64 * 65536), Next(64 * 65536), Next(64 * 65536));
			var expected = q * v;
			var actual = C.ContactSolverSystem.MakeDeltaRotationMatrix(q) * v;
			Assert.That(actual.Equals(expected), Is.True, $"rotation raw result {i}");
		}
	}

	[Test]
	public void SolverMatrixProductMatchesRawLibraryResults() {
		var random = new Random(916721);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			Fixed32.FVector3 Vector() => new(Next(), Next(), Next());
			var matrix = new Fixed32.FMatrix3(Vector(), Vector(), Vector());
			var vector = Vector();
			var expected = matrix * vector;
			var actual = C.ContactSolverSystem.MultiplySolverMatrix(matrix, vector);
			Assert.That(actual.Equals(expected), Is.True, $"matrix raw result {i}");
		}
	}

	[Test]
	public void SolverCrossProductMatchesRawLibraryResults() {
		var random = new Random(294617);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			Fixed32.FVector3 Vector() => new(Next(), Next(), Next());
			var a = Vector();
			var b = Vector();
			var expected = Fixed32.FVector3.Cross(a, b);
			var actual = C.ContactSolverSystem.CrossSolverVectors(a, b);
			Assert.That(actual.Equals(expected), Is.True, $"cross raw result {i}");
		}
	}

	[Test]
	public void SolverVectorSubtractionMatchesRawLibraryResults() {
		var random = new Random(460217);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			Fixed32.FVector3 Vector() => new(Next(), Next(), Next());
			var a = Vector();
			var b = Vector();
			var expected = a - b;
			var actual = C.ContactSolverSystem.SubtractSolverVectors(a, b);
			Assert.That(actual.Equals(expected), Is.True, $"vector subtraction raw result {i}");
		}
	}

	[Test]
	public void SolverVectorAdditionMatchesRawLibraryResults() {
		var random = new Random(617209);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			Fixed32.FVector3 Vector() => new(Next(), Next(), Next());
			var a = Vector();
			var b = Vector();
			var expected = a + b;
			var actual = C.ContactSolverSystem.AddSolverVectors(a, b);
			Assert.That(actual.Equals(expected), Is.True, $"vector addition raw result {i}");
		}
	}

	[Test]
	public void SolverVectorScalingMatchesRawLibraryResults() {
		var random = new Random(903617);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			var vector = new Fixed32.FVector3(Next(), Next(), Next());
			var scale = Next();
			var actual = C.ContactSolverSystem.ScaleSolverVector(vector, scale);
			Assert.That(actual.Equals(vector * scale), Is.True, $"vector/scale raw result {i}");
			Assert.That(actual.Equals(scale * vector), Is.True, $"scale/vector raw result {i}");
		}
	}

	[Test]
	public void SolverDotProductMatchesRawLibraryResults() {
		var random = new Random(418903);
		int[] edges = [int.MinValue, int.MaxValue, -65537, -65536, -65535, -1, 0, 1, 65535, 65536, 65537];
		for (var i = 0; i < 8192; i++) {
			Fixed32.FP Next() => Fixed32.FP.FromRaw(i < 512
				? edges[random.Next(edges.Length)] : (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
			Fixed32.FVector3 Vector() => new(Next(), Next(), Next());
			var a = Vector();
			var b = Vector();
			var expected = Fixed32.FVector3.Dot(a, b);
			var actual = C.ContactSolverSystem.DotSolverVectors(a, b);
			Assert.That(actual.RawValue, Is.EqualTo(expected.RawValue), $"dot raw result {i}");
		}
	}

	[Test]
	public void AuthoredLabMatchesEveryPreDTickHash() {
		var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "Client/maps/level_pipeline_test.level.bytes"))) {
			root = root.Parent;
		}
		Assert.That(root, Is.Not.Null);
		var level = LevelFile.ReadFromDisk(Path.Combine(root!.FullName, "Client/maps/level_pipeline_test.level.bytes")).Data;
		NavTestSession.CreateWorld<PackedSolverWorld>(level);
		var playerGid = C.W.NewEntity(new Player { PlayerGuid = Guid.Parse("609a190b-e3cc-4904-b74c-2f8e5d4cbab0"), InputChannel = 1 }).GID;
		Assert.That(playerGid.TryUnpack<PackedSolverWorld>(out var player), Is.True);
		player.Ref<Transform>().Position = new F.FVector3(9 * F.FP.One, F.FP.FromRatio(3, 2), -33 * F.FP.One);
		var hashes = new ulong[480];
		var writer = NavTestSession.SnapshotWriter<PackedSolverWorld>();
		for (var tick = 0; tick < hashes.Length; tick++) {
			C.S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
			NavTestSession.Step<PackedSolverWorld>();
			hashes[tick] = NavTestSession.WorldHash<PackedSolverWorld>(ref writer);
		}
		var expected = Convert.FromBase64String(PreDHashes);
		Assert.That(expected.Length, Is.EqualTo(hashes.Length * sizeof(ulong)));
		for (var tick = 0; tick < hashes.Length; tick++) {
			Assert.That(hashes[tick], Is.EqualTo(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(expected.AsSpan(tick * sizeof(ulong)))), $"pre-D world snapshot at tick {tick}");
		}
	}

	[Test]
	public void RestoredWorldRebuildsPoisonedScratchAndRetainsNoHandlesBetweenTicks() {
		NavTestSession.CreateWorld<PackedSolverRestoreWorld>(NavTestSession.Level());
		var bodyGid = World<PackedSolverRestoreWorld>.NewEntity<Default>().GID;
		Assert.That(bodyGid.TryUnpack<PackedSolverRestoreWorld>(out var body), Is.True);
		Core<PackedSolverRestoreWorld>.BodyOperations.CreateBody(body, BodyType.Dynamic,
			new Fixed.FWorldTransform(new Fixed.FPos(20 * F.FP.One, F.FP.One, 20 * F.FP.One), Fixed32.FQuaternion.Identity));
		Core<PackedSolverRestoreWorld>.ShapeFactory.CreateShape(body,
			Shape.MakeBox(Fixed32.FVector3.Zero, new Fixed32.FVector3(Fixed32.FP.Half, Fixed32.FP.Half, Fixed32.FP.Half)));
		Core<PackedSolverRestoreWorld>.BodyOperations.SetSleepEnabled(body, false);
		for (var tick = 0; tick < 120; tick++) {
			NavTestSession.Step<PackedSolverRestoreWorld>();
		}
		var snapshot = World<PackedSolverRestoreWorld>.Serializer.CreateWorldSnapshot();
		var writer = NavTestSession.SnapshotWriter<PackedSolverRestoreWorld>();
		var hashes = new ulong[30];
		for (var tick = 0; tick < hashes.Length; tick++) {
			StepPhysics();
			hashes[tick] = NavTestSession.WorldHash<PackedSolverRestoreWorld>(ref writer);
		}
		World<PackedSolverRestoreWorld>.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		var runtime = Core<PackedSolverRestoreWorld>.PhysicsRuntime.Get();
		// This resource intentionally survives hard reset. Garbage packed state must never become
		// authoritative, and the next step must rebuild rather than trust old buffer indices.
		runtime.SolverStates.Add(new Core<PackedSolverRestoreWorld>.ContactSolverSystem.SolverBodyState {
			LinearVelocity = new Fixed32.FVector3(50 * Fixed32.FP.One, 50 * Fixed32.FP.One, 50 * Fixed32.FP.One)
		});
		Assert.That(bodyGid.TryUnpack<PackedSolverRestoreWorld>(out var restored), Is.True);
		runtime.BodyIndices.Add(restored, int.MaxValue);
		runtime.SolverStateBodies.Add(restored);
		runtime.SolverBodies.Add(restored);
		for (var tick = 0; tick < hashes.Length; tick++) {
			StepPhysics();
			Assert.That(NavTestSession.WorldHash<PackedSolverRestoreWorld>(ref writer), Is.EqualTo(hashes[tick]), $"restored packed tick {tick}");
			Assert.Multiple(() => {
				Assert.That(runtime.Stats.AwakeBodies, Is.GreaterThan(0));
				Assert.That(runtime.Stats.Constraints, Is.GreaterThan(0), "restored contact must actually exercise packed indices/scatter");
				Assert.That(runtime.SolverStates, Is.Empty);
				Assert.That(runtime.SolverInputs, Is.Empty);
				Assert.That(runtime.SolverBodies, Is.Empty);
				Assert.That(runtime.SolverStateBodies, Is.Empty);
				Assert.That(runtime.SolverConstraints, Is.Empty);
				Assert.That(runtime.BodyIndices, Is.Empty);
				Assert.That(runtime.IslandEdges, Is.Empty);
			});
		}
	}

	private static void StepPhysics() {
		World<PackedSolverRestoreWorld>.Tick();
		new Core<PackedSolverRestoreWorld>.ShapeProxySystem().Update();
		new Core<PackedSolverRestoreWorld>.ContactSystem().Update();
		new Core<PackedSolverRestoreWorld>.ContactSolverSystem().Update();
	}
}
