using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using Fixed;
using NUnit.Framework;
using static Space.GameCore.Core<Space.GameCore.Tests.PhysicsToyWorld>;
using F = Fixed64;
using WP = FFS.Libraries.StaticEcs.World<Space.GameCore.Tests.PhysicsLabPreviousWorld>;

namespace Space.GameCore.Tests;

public struct PhysicsLabPreviousWorld : IWorldType;

/// <summary>Opt-in timing probe of the authored lab; run in Release on an idle machine.</summary>
[TestFixture, NonParallelizable, Explicit("Wall-clock diagnostic; not a portable CI timing test")]
public sealed class PhysicsLabPerformanceTests {
	private readonly record struct BoxPair(Shape A, FWorldTransform XfA, Shape B, FWorldTransform XfB);
	private static readonly List<BoxPair> s_boxPairs = [];
	private static bool s_collectPairs;
	private delegate Manifold Collider(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB);
	private struct CaptureBoxPairs : ISystem {
		public void Update() {
			if (!s_collectPairs)
				return;
			foreach (var e in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref e.Read<Contact>();
				if (!contact.ShapeA.TryUnpack<PhysicsToyWorld>(out var a) || !contact.ShapeB.TryUnpack<PhysicsToyWorld>(out var b))
					continue;
				ref readonly var shapeA = ref a.Read<Shape>();
				ref readonly var shapeB = ref b.Read<Shape>();
				if (shapeA.Type != ShapeType.Hull || shapeB.Type != ShapeType.Hull)
					continue;
				if (!a.Read<W.Link<BodyOwner>>().Value.TryUnpack<PhysicsToyWorld>(out var ownerA)
					|| !b.Read<W.Link<BodyOwner>>().Value.TryUnpack<PhysicsToyWorld>(out var ownerB))
					continue;
				ref readonly var bodyA = ref ownerA.Read<Body>();
				ref readonly var bodyB = ref ownerB.Read<Body>();
				if (!PhysicsSleep.IsAwake(bodyA) && !PhysicsSleep.IsAwake(bodyB))
					continue;
				s_boxPairs.Add(new BoxPair(shapeA, bodyA.Transform, shapeB, bodyB.Transform));
			}
		}
	}

	[Test]
	public void ActiveLabBoxNarrowphaseMatchesAndImprovesOriginal() {
		var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "Client/maps/level_pipeline_test.level.bytes")))
			root = root.Parent;
		Assert.That(root, Is.Not.Null);
		var level = LevelFile.ReadFromDisk(Path.Combine(root!.FullName, "Client/maps/level_pipeline_test.level.bytes")).Data;
		s_boxPairs.Clear();
		NavTestSession.CreateWorld<PhysicsToyWorld>(level, registerSystems: () => Systems.Add(new CaptureBoxPairs(), order: 11));
		var player = W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 1 });
		player.Ref<Transform>().Position = new F.FVector3(F.FP.FromRatio(9, 1), F.FP.FromRatio(3, 2), F.FP.FromRatio(-33, 1));
		for (var tick = 0; tick < 240; tick++) {
			s_collectPairs = tick >= 120;
			S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
			NavTestSession.Step<PhysicsToyWorld>();
		}
		s_collectPairs = false;
		NavTestSession.DestroyIfCreated<PhysicsToyWorld>();
		Assert.That(s_boxPairs.Count, Is.GreaterThan(100), "active box contact poses must be captured");
		foreach (var pair in s_boxPairs) {
			var expected = BoxSatReference.Collide(pair.A, pair.XfA, pair.B, pair.XfB);
			var actual = Manifold.Collide(pair.A, pair.XfA, pair.B, pair.XfB);
			Assert.That(actual, Is.EqualTo(expected), "entire active-lab manifold, including feature IDs and unused slots");
			Assert.That(BoxSatReference.CollideWithOptimizedSat(pair.A, pair.XfA, pair.B, pair.XfB), Is.EqualTo(actual), "timed shared glue must match production");
		}
		// Time just the changed SAT edge query with equivalent dispatch, not the reference's
		// delegate-based contact builders versus production's direct calls.
		var queries = s_boxPairs.Select(pair => {
			var xf = FWorldTransform.InvMul(pair.XfA, pair.XfB);
			var inverseA = Fixed32.FQuaternion.Inverse(pair.A.HullShape.Rotation);
			return (HeA: pair.A.HullShape.HalfExtents,
				CenterB: inverseA * (Fixed32.FTransform.TransformPoint(xf, pair.B.HullShape.Center) - pair.A.HullShape.Center),
				RotationB: inverseA * (xf.Rotation * pair.B.HullShape.Rotation), HeB: pair.B.HullShape.HalfExtents);
		}).ToArray();
		BoxSatReference.EdgeQuery original = BoxSatReference.QueryEdge, optimized = BoxSatReference.OptimizedEdge;
		foreach (var q in queries)
			Assert.That(optimized(q.HeA, q.CenterB, q.RotationB, q.HeB), Is.EqualTo(original(q.HeA, q.CenterB, q.RotationB, q.HeB)));
		(long checksum, double ms) Batch(BoxSatReference.EdgeQuery query) {
			long checksum = 0;
			var start = Stopwatch.GetTimestamp();
			foreach (var q in queries) {
				var result = query(q.HeA, q.CenterB, q.RotationB, q.HeB);
				checksum += result.EdgeA + result.EdgeB + result.Separation.RawValue;
			}
			return (checksum, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		}
		for (var i = 0; i < 12; i++) { Batch(original); Batch(optimized); }
		Thread.Sleep(1000); // Allow tiered JIT promotion before measuring either implementation.
		var oldMs = new double[9];
		var newMs = new double[9];
		for (var i = 0; i < oldMs.Length; i++) {
			var first = Batch(i % 2 == 0 ? original : optimized);
			var second = Batch(i % 2 == 0 ? optimized : original);
			Assert.That(first.checksum, Is.EqualTo(second.checksum));
			oldMs[i] = i % 2 == 0 ? first.ms : second.ms;
			newMs[i] = i % 2 == 0 ? second.ms : first.ms;
		}
		Array.Sort(oldMs);
		Array.Sort(newMs);
		TestContext.WriteLine($"active lab ticks=120-239 box evaluations={s_boxPairs.Count}: edge SAT original median={oldMs[4]:F3}ms optimized median={newMs[4]:F3}ms ratio={newMs[4] / oldMs[4]:F3}; original range={oldMs[0]:F3}-{oldMs[^1]:F3} optimized range={newMs[0]:F3}-{newMs[^1]:F3}");
		Assert.That(newMs[4], Is.LessThan(oldMs[4]), "paired warmed box edge SAT should improve; this is an explicit diagnostic, not a CI timing gate");
		// Full box-manifold evaluation with identical contact-builder/transform dispatch in both
		// variants. Only the selected SAT queries differ; ECS traversal/point matching is excluded.
		Collider oldCollider = BoxSatReference.Collide, newCollider = BoxSatReference.CollideWithOptimizedSat;
		(long checksum, double ms) ManifoldBatch(Collider collide) {
			long checksum = 0;
			var start = Stopwatch.GetTimestamp();
			foreach (var pair in s_boxPairs) {
				var m = collide(pair.A, pair.XfA, pair.B, pair.XfB);
				checksum += m.PointCount + m.Point0.FeatureId;
			}
			return (checksum, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		}
		for (var i = 0; i < 12; i++) { ManifoldBatch(oldCollider); ManifoldBatch(newCollider); }
		Thread.Sleep(1000);
		for (var i = 0; i < oldMs.Length; i++) {
			var first = ManifoldBatch(i % 2 == 0 ? oldCollider : newCollider);
			var second = ManifoldBatch(i % 2 == 0 ? newCollider : oldCollider);
			Assert.That(first.checksum, Is.EqualTo(second.checksum));
			oldMs[i] = i % 2 == 0 ? first.ms : second.ms;
			newMs[i] = i % 2 == 0 ? second.ms : first.ms;
		}
		Array.Sort(oldMs);
		Array.Sort(newMs);
		TestContext.WriteLine($"active lab box manifolds (same glue): original median={oldMs[4]:F3}ms optimized median={newMs[4]:F3}ms ratio={newMs[4] / oldMs[4]:F3}; original range={oldMs[0]:F3}-{oldMs[^1]:F3} optimized range={newMs[0]:F3}-{newMs[^1]:F3}");
		Assert.That(newMs[4], Is.LessThan(oldMs[4]), "paired warmed box manifold evaluation should improve");
	}
	private static long s_moverStart;
	private static double s_moverMs;
	private struct StartMover : ISystem {
		public void Update() => s_moverStart = Stopwatch.GetTimestamp();
	}
	private struct EndMover : ISystem {
		public void Update() => s_moverMs = Stopwatch.GetElapsedTime(s_moverStart).TotalMilliseconds;
	}

	[TearDown]
	public void TearDown() {
		s_collectPairs = false;
		s_boxPairs.Clear();
		if (WP.Status == WorldStatus.Initialized)
			WP.Destroy();
		NavTestSession.DestroyIfCreated<PhysicsToyWorld>();
	}

	[TestCase(true)]
	[TestCase(false)]
	public void AuthoredLabTickBudget(bool includeStressProps) {
		var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "Client/maps/level_pipeline_test.level.bytes")))
			root = root.Parent;
		Assert.That(root, Is.Not.Null);
		var sample = LevelFile.ReadFromDisk(Path.Combine(root!.FullName, "Client/maps/level_pipeline_test.level.bytes")).Data;
		var level = includeStressProps ? sample : new LevelData(sample.Entities.Where(static e => !e.SourcePath.StartsWith("PhysicsStress/", StringComparison.Ordinal)), sample.Navigation, sample.NavZones);
		var budgetMiss = false;
		for (var run = 0; run < 3; run++) {
			budgetMiss |= MeasureRun(level, includeStressProps, run);
		}
		Assert.That(budgetMiss, Is.False, "physics + mover exceeded the documented 1ms regular-tick budget during the push/settling run");
	}

	private static bool MeasureRun(LevelData level, bool includeStressProps, int run) {
		var budgetMiss = false;
		NavTestSession.CreateWorld<PhysicsToyWorld>(level, registerSystems: () => {
			Systems.Add(new StartMover(), order: 5);
			Systems.Add(new EndMover(), order: 6);
		});
		var gid = W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 1 }).GID;
		Assert.That(gid.TryUnpack<PhysicsToyWorld>(out var player), Is.True);
		player.Ref<Transform>().Position = new F.FVector3(F.FP.FromRatio(9, 1), F.FP.FromRatio(3, 2), F.FP.FromRatio(-33, 1));
		for (var window = 0; window < 4; window++) {
			var times = new double[120];
			var phases = new double[(int)PhysicsPhase.Count];
			double mover = 0, awake = 0, sleeping = 0, constraints = 0, ccd = 0;
			var allocated = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < times.Length; i++) {
				var tick = window * times.Length + i;
				S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
				var start = Stopwatch.GetTimestamp();
				NavTestSession.Step<PhysicsToyWorld>();
				times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
				var stats = PhysicsDiagnostics.LastStep;
				for (var p = 0; p < phases.Length; p++)
					phases[p] += stats.Milliseconds((PhysicsPhase)p);
				mover += s_moverMs;
				awake += stats.AwakeBodies;
				sleeping += stats.SleepingBodies;
				constraints += stats.Constraints;
				ccd += stats.ContinuousBodies;
			}
			allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
			var average = times.Average();
			Array.Sort(times);
			TestContext.WriteLine($"run={run} stress={includeStressProps} ticks={window * 120}-{(window + 1) * 120 - 1}: session={average:F3}ms p95={times[114]:F3} max={times[^1]:F3} mover={mover / 120:F3} physics={phases.Sum() / 120:F3} awake={awake / 120:F1} asleep={sleeping / 120:F1} constraints={constraints / 120:F1} ccd={ccd / 120:F1} alloc={allocated / 120}B/tick");
			TestContext.WriteLine(string.Join(" ", phases.Select((ms, p) => $"{(PhysicsPhase)p}={ms / 120:F3}ms")));
			// Check the third replay to exclude startup/JIT; use the documented regular-tick budget.
			if (run == 2 && (window is 1 or 2) && (phases.Sum() + mover) / 120 > 1.0)
				budgetMiss = true;
		}
		TestContext.WriteLine($"counts: {PhysicsDiagnostics.Capture()}; trees: {PhysicsDiagnostics.CaptureBroadPhase()}");
		if (run == 2)
			MeasureInterpolation();
		NavTestSession.DestroyIfCreated<PhysicsToyWorld>();
		return budgetMiss;
	}

	private static void MeasureInterpolation() {
		WP.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<PhysicsLabPreviousWorld>();
		WP.Initialize();
		var writer = NavTestSession.SnapshotWriter<PhysicsToyWorld>();
		void Copy() {
			writer.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref writer);
			var reader = writer.AsReader();
			WP.Serializer.LoadWorldSnapshot(ref reader, true);
		}
		for (var i = 0; i < 100; i++)
			Copy();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var start = Stopwatch.GetTimestamp();
		for (var i = 0; i < 300; i++)
			Copy();
		var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 300;
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		TestContext.WriteLine($"interpolation world copy: {milliseconds:F3}ms/call {allocated / 300}B/call snapshot={writer.Position}B");
		WP.Destroy();
	}
}
