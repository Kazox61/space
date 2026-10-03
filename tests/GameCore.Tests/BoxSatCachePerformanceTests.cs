using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using static Space.GameCore.Core<Space.GameCore.Tests.PhysicsToyWorld>;
using F = Fixed64;

namespace Space.GameCore.Tests;

/// <summary>Paired warmed narrowphase evaluation of the same pre-contact lab poses and histories.</summary>
[TestFixture, NonParallelizable, Explicit("Wall-clock diagnostic; run on an idle machine")]
public sealed class BoxSatCachePerformanceTests {
	private readonly record struct Sample(Shape A, FWorldTransform XfA, Shape B, FWorldTransform XfB, BoxSatCache Cache, EntityGID AId, EntityGID BId);
	private static readonly List<Sample>[] s_samples = [[], []];
	private static int s_window = -1;
	private struct Capture : ISystem {
		public void Update() {
			if (s_window < 0)
				return;
			foreach (var e in W.Query<All<Contact>>().Entities()) {
				ref readonly var c = ref e.Read<Contact>();
				if (!c.ShapeA.TryUnpack<PhysicsToyWorld>(out var a) || !c.ShapeB.TryUnpack<PhysicsToyWorld>(out var b))
					continue;
				ref readonly var sa = ref a.Read<Shape>();
				ref readonly var sb = ref b.Read<Shape>();
				if (sa.Type != ShapeType.Hull || sb.Type != ShapeType.Hull || !FAABB.Overlaps(sa.FatAabb, sb.FatAabb))
					continue;
				if (!a.Read<W.Link<BodyOwner>>().Value.TryUnpack<PhysicsToyWorld>(out var ba) || !b.Read<W.Link<BodyOwner>>().Value.TryUnpack<PhysicsToyWorld>(out var bb))
					continue;
				if (!PhysicsSleep.IsAwake(ba.Read<Body>()) && !PhysicsSleep.IsAwake(bb.Read<Body>()))
					continue;
				s_samples[s_window].Add(new(sa, ba.Read<Body>().Transform, sb, bb.Read<Body>().Transform, c.SatCache, a.GID, b.GID));
			}
		}
	}
	[TearDown]
	public void TearDown() {
		s_window = -1;
		foreach (var samples in s_samples)
			samples.Clear();
		NavTestSession.DestroyIfCreated<PhysicsToyWorld>();
	}

	[Test]
	public void PairedActiveAndSettledLabFeatureCache() {
		var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "Client/maps/level_pipeline_test.level.bytes")))
			root = root.Parent;
		Assert.That(root, Is.Not.Null);
		var level = LevelFile.ReadFromDisk(Path.Combine(root!.FullName, "Client/maps/level_pipeline_test.level.bytes")).Data;
		for (var run = 0; run < 3; run++) {
			NavTestSession.CreateWorld<PhysicsToyWorld>(level, registerSystems: () => Systems.Add(new Capture(), order: 9));
			var player = W.NewEntity(new Player { PlayerGuid = Guid.Parse("66369dfa-3f62-4e1c-95d9-fac7df1c6233"), InputChannel = 1 });
			player.Ref<Transform>().Position = new F.FVector3(9 * F.FP.One, F.FP.FromRatio(3, 2), -33 * F.FP.One);
			var tickMs = new double[4];
			var narrowMs = new double[4];
			var physicsMs = new double[4];
			var counters = new long[4, 6];
			for (var tick = 0; tick < 480; tick++) {
				s_window = run == 2 ? tick is >= 120 and < 240 ? 0 : tick >= 360 ? 1 : -1 : -1;
				S.SetApprovedInput(1, new PlayerInput { MoveX = tick < 160 ? F.FP.One : F.FP.Zero });
				var start = Stopwatch.GetTimestamp();
				NavTestSession.Step<PhysicsToyWorld>();
				var w = tick / 120;
				tickMs[w] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
				var stats = PhysicsDiagnostics.LastStep;
				narrowMs[w] += stats.Milliseconds(PhysicsPhase.Narrowphase);
				physicsMs[w] += stats.TotalMilliseconds;
				counters[w, 0] += stats.BoxSatFullSearches;
				counters[w, 1] += stats.BoxSatSeparationHits;
				counters[w, 2] += stats.BoxSatFaceHits;
				counters[w, 3] += stats.BoxSatEdgeHits;
				counters[w, 4] += stats.ContactsSleeping;
				counters[w, 5] += stats.BoxSatFallbacks;
			}
			for (var w = 0; w < 4; w++)
				TestContext.WriteLine($"C lab run={run} ticks={w * 120}-{w * 120 + 119} session={tickMs[w] / 120:F3}ms physics={physicsMs[w] / 120:F3}ms narrowphase={narrowMs[w] / 120:F3}ms full/separation/face/edge/sleeping={counters[w, 0]}/{counters[w, 1]}/{counters[w, 2]}/{counters[w, 3]}/{counters[w, 4]} fallbacks={counters[w, 5]}");
			NavTestSession.DestroyIfCreated<PhysicsToyWorld>();
		}
		for (var w = 0; w < 2; w++)
			Measure(s_samples[w], w == 0 ? "active 120-239" : "settled 360-479");
	}

	private static long s_checksum;
	private static void Measure(List<Sample> samples, string window) {
		Assert.That(samples.Count, Is.GreaterThan(0));
		var counts = new int[5];
		foreach (var s in samples) {
			var cache = s.Cache;
			var m = Manifold.Collide(s.A, s.XfA, s.B, s.XfB, ref cache, out var result, s.AId, s.BId);
			counts[(int)result]++;
			if (result == BoxSatResult.FullSearch)
				Assert.That(m, Is.EqualTo(Manifold.Collide(s.A, s.XfA, s.B, s.XfB)));
		}
		double Batch(bool cached) {
			long checksum = 0;
			var start = Stopwatch.GetTimestamp();
			foreach (var s in samples) {
				var cache = s.Cache;
				var m = cached ? Manifold.Collide(s.A, s.XfA, s.B, s.XfB, ref cache, out _, s.AId, s.BId) : Manifold.Collide(s.A, s.XfA, s.B, s.XfB);
				checksum += m.PointCount + m.Point0.FeatureId;
			}
			s_checksum = checksum;
			return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		}
		for (var i = 0; i < 16; i++) { Batch(false); Batch(true); }
		Thread.Sleep(1000);
		var full = new double[9];
		var reuse = new double[9];
		for (var i = 0; i < 9; i++) {
			if (i % 2 == 0) { full[i] = Batch(false); reuse[i] = Batch(true); } else { reuse[i] = Batch(true); full[i] = Batch(false); }
		}
		Array.Sort(full);
		Array.Sort(reuse);
		TestContext.WriteLine($"C paired {window}: poses={samples.Count} full/separation/face/edge={string.Join('/', counts.Skip(1))}; full median={full[4]:F3}ms range={full[0]:F3}-{full[^1]:F3}; cached median={reuse[4]:F3}ms range={reuse[0]:F3}-{reuse[^1]:F3}; ratio={reuse[4] / full[4]:F3}; checksum={s_checksum}");
	}
}
