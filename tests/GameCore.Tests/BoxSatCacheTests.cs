using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using NUnit.Framework;
using Shenanicode.Rollback;
using static Space.GameCore.Tests.NavTestSession;
using C = Space.GameCore.Core<Space.GameCore.Tests.BoxCacheLifecycleWorld>;

namespace Space.GameCore.Tests;

public struct BoxCacheLifecycleWorld : IWorldType, ISessionType;
public struct BoxCacheReplayWorld : IWorldType, ISessionType;
public struct BoxCacheSyncWorld : IWorldType, ISessionType;

[TestFixture, NonParallelizable]
public sealed class BoxSatCacheTests {
	private static Shape Box => Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Half, FP.Half));
	private static FWorldTransform Pose(double x = 0, double y = 0, double z = 0, FQuaternion? q = null) =>
		new(new FPos(x.ToFP().To64(), y.ToFP().To64(), z.ToFP().To64()), q ?? FQuaternion.Identity);

	[TearDown]
	public void TearDown() {
		DestroyIfCreated<BoxCacheLifecycleWorld>();
		DestroyIfCreated<BoxCacheReplayWorld>();
		DestroyIfCreated<BoxCacheSyncWorld>();
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FaceReuseRetainsBaselineAndFallsBackOnAccumulatedDrift(bool faceB) {
		var box = Box;
		var cache = new BoxSatCache();
		var initial = Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out var result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
		if (faceB) {
			cache.Axis = BoxSatAxis.FaceB;
			cache.IndexB = Array.FindIndex(Hull.Faces, f => f.Normal == -FVector3.Up);
		}
		var baseline = cache;
		var same = Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FaceHit));
		Assert.That(same.PointCount, Is.EqualTo(4));
		Assert.That(same.MinSeparation(), Is.EqualTo(initial.MinSeparation()));
		Assert.That(Bytes(cache), Is.EqualTo(Bytes(baseline)));
		var smallMove = B3Config.LinearSlop / 2;
		var xf = Pose(y: 0.99);
		xf.Position.Y += smallMove.To64();
		Manifold.Collide(box, Pose(), box, xf, ref cache, out result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FaceHit));
		Assert.That(cache.Separation, Is.EqualTo(baseline.Separation), "Box3D retains original baseline on hits");
		xf.Position.Y += B3Config.LinearSlop.To64();
		var fallback = Manifold.Collide(box, Pose(), box, xf, ref cache, out result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
		Assert.That(fallback, Is.EqualTo(Manifold.Collide(box, Pose(), box, xf)), "fallback remains B's oracle path");
	}

	[Test]
	public void CachedFaceSeparationThenContactTransition() {
		var box = Box;
		var cache = new BoxSatCache();
		Manifold.Collide(box, Pose(), box, Pose(y: 2), ref cache, out _);
		var baseline = cache;
		var empty = Manifold.Collide(box, Pose(), box, Pose(y: 3), ref cache, out var result);
		Assert.That(result, Is.EqualTo(BoxSatResult.SeparationHit));
		Assert.That(empty.PointCount, Is.Zero);
		Assert.That(Bytes(cache), Is.EqualTo(Bytes(baseline)));
		var touching = Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
		Assert.That(touching.PointCount, Is.EqualTo(4));
		Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FaceHit));
	}

	[TestCase(-1)]
	[TestCase(6)]
	[TestCase(1000)]
	public void InvalidFaceIndexFallsBack(int index) {
		var box = Box;
		var cache = new BoxSatCache();
		Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out _);
		cache.IndexA = index;
		var actual = Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out var result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
		Assert.That(actual, Is.EqualTo(Manifold.Collide(box, Pose(), box, Pose(y: 0.99))));
	}

	[Test]
	public void EmptyClipAndInvalidOrParallelEdgesDiscardCandidateBeforeFallback() {
		var box = Box;
		var cache = new BoxSatCache();
		Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out _);
		var xf = Pose(x: 3, y: 0.99);
		var actual = Manifold.Collide(box, Pose(), box, xf, ref cache, out var result);
		Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch), "cached face overlaps on its normal but clipping misses");
		Assert.That(actual, Is.EqualTo(Manifold.Collide(box, Pose(), box, xf)));
		foreach (var index in new[] { 0, -1, 12 }) {
			cache.Axis = BoxSatAxis.EdgePair;
			cache.IndexA = index;
			cache.IndexB = index;
			actual = Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out result);
			Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
			Assert.That(actual, Is.EqualTo(Manifold.Collide(box, Pose(), box, Pose(y: 0.99))));
		}
	}

	[Test]
	public void GeometryAndOrderedIdentityChangesInvalidateAndRoundPairsClearCache() {
		var box = Box;
		foreach (var field in new[] { "center", "rotation", "extents" }) {
			var cache = new BoxSatCache();
			Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref cache, out _);
			var changed = box;
			if (field == "center")
				changed.HullShape.Center.X = FP.FromRatio(1, 100);
			if (field == "rotation")
				changed.HullShape.Rotation = FQuaternion.Normalize(new FQuaternion(0.01.ToFP(), FP.Zero, FP.Zero, FP.One));
			if (field == "extents")
				changed.HullShape.HalfExtents.X += FP.FromRatio(1, 100);
			var actual = Manifold.Collide(changed, Pose(), box, Pose(y: 0.99), ref cache, out var result);
			Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
			Assert.That(actual, Is.EqualTo(Manifold.Collide(changed, Pose(), box, Pose(y: 0.99))));
		}
		CreateWorld<BoxCacheLifecycleWorld>(new LevelData([]));
		var a = C.W.NewEntity<Default>().GID;
		var b = C.W.NewEntity<Default>().GID;
		var identityCache = new BoxSatCache();
		Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref identityCache, out _, a, b);
		Manifold.Collide(box, Pose(), box, Pose(y: 0.99), ref identityCache, out var swapped, b, a);
		Assert.That(swapped, Is.EqualTo(BoxSatResult.FullSearch), "even identical boxes cannot reuse a swapped ordered identity");
		Manifold.Collide(Shape.MakeSphere(FVector3.Zero, FP.Half), Pose(), box, Pose(), ref identityCache, out var round);
		Assert.That(round, Is.EqualTo(BoxSatResult.NotBoxPair));
		Assert.That(Bytes(identityCache), Is.EqualTo(Bytes(default(BoxSatCache))));
	}

	[Test]
	public void DeterministicEdgeCorpusHitsAndInvalidatesAfterRotation() {
		var box = Box;
		var random = new Random(912763);
		var edgeHits = 0;
		var separationHits = 0;
		for (var i = 0; i < 4096; i++) {
			var q = FQuaternion.Normalize(new FQuaternion((random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), FP.One));
			var xf = Pose(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, q);
			var cache = new BoxSatCache();
			var first = Manifold.Collide(box, Pose(), box, xf, ref cache, out _);
			Assert.That(first, Is.EqualTo(BoxSatReference.Collide(box, Pose(), box, xf)));
			if (cache.Axis != BoxSatAxis.EdgePair)
				continue;
			var original = cache;
			var second = Manifold.Collide(box, Pose(), box, xf, ref cache, out var result);
			Assert.That(second, Is.EqualTo(first));
			Assert.That(Bytes(cache), Is.EqualTo(Bytes(original)));
			if (result == BoxSatResult.EdgeHit)
				edgeHits++;
			if (result == BoxSatResult.SeparationHit)
				separationHits++;
			xf.Rotation = FQuaternion.Identity;
			var fallback = Manifold.Collide(box, Pose(), box, xf, ref cache, out result);
			Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch), "parallel edges invalidate the previous Gauss-map feature");
			Assert.That(fallback, Is.EqualTo(Manifold.Collide(box, Pose(), box, xf)));
		}
		Assert.That(edgeHits, Is.GreaterThan(20));
		Assert.That(separationHits, Is.GreaterThan(0));
		TestContext.WriteLine($"edge corpus hits={edgeHits}, separating-edge hits={separationHits}");
	}

	[Test]
	public void MovingFeatureDecisionsMatchReferenceValidityRules() {
		var random = new Random(810343);
		var box = Box;
		var decisions = new int[5];
		var faceBHits = 0;
		for (var i = 0; i < 1024; i++) {
			var q = FQuaternion.Normalize(new FQuaternion((random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), FP.One));
			var xf = Pose(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, q);
			var cache = new BoxSatCache();
			var a = box;
			var b = box;
			a.HullShape.Center = new FVector3(.1.ToFP(), -.2.ToFP(), .15.ToFP());
			a.HullShape.Rotation = FQuaternion.Normalize(new FQuaternion(.05.ToFP(), .1.ToFP(), -.1.ToFP(), FP.One));
			b.HullShape.Center = -a.HullShape.Center;
			b.HullShape.Rotation = FQuaternion.Normalize(new FQuaternion(-.1.ToFP(), .03.ToFP(), .07.ToFP(), FP.One));
			var bodyA = Pose(.3, -.1, .2, q);
			for (var frame = 0; frame < 12; frame++) {
				var aa = i % 2 == 0 ? a : b;
				var bb = i % 2 == 0 ? b : a;
				var xa = i % 2 == 0 ? bodyA : xf;
				var xb = i % 2 == 0 ? xf : bodyA;
				var expected = BoxSatCacheReference.Collide(aa, xa, bb, xb, cache, out var expectedManifold, out _);
				var m = Manifold.Collide(aa, xa, bb, xb, ref cache, out var actual);
				Assert.That(actual, Is.EqualTo(expected), $"reference validity decision pose={i} frame={frame}");
				if (actual == BoxSatResult.FullSearch)
					Assert.That(m, Is.EqualTo(BoxSatReference.Collide(aa, xa, bb, xb)));
				else
					Assert.That(m, Is.EqualTo(expectedManifold), "complete cached-feature reconstruction, not cold-SAT winner");
				if (actual == BoxSatResult.FaceHit && cache.Axis == BoxSatAxis.FaceB)
					faceBHits++;
				decisions[(int)actual]++;
				xf.Position.X += FP.FromRatio(1, 1000).To64();
				xf.Position.Y += FP.FromRatio(1, 1000).To64();
			}
		}
		Assert.That(decisions[(int)BoxSatResult.EdgeHit], Is.GreaterThan(10));
		Assert.That(decisions[(int)BoxSatResult.FaceHit], Is.GreaterThan(100));
		Assert.That(faceBHits, Is.GreaterThan(100));
		TestContext.WriteLine($"reference decisions full/separation/face/edge={string.Join('/', decisions.Skip(1))}");
	}

	[Test]
	public void EdgeSegmentAndBaselineDriftRejectionsFallBackToFullSearch() {
		var random = new Random(912763);
		var box = Box;
		var segmentRejects = 0;
		var driftRejects = 0;
		for (var i = 0; i < 4096 && (segmentRejects == 0 || driftRejects == 0); i++) {
			var q = FQuaternion.Normalize(new FQuaternion((random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), (random.NextDouble() - .5).ToFP(), FP.One));
			var xf = Pose(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, q);
			var cache = new BoxSatCache();
			Manifold.Collide(box, Pose(), box, xf, ref cache, out _);
			if (cache.Axis != BoxSatAxis.EdgePair)
				continue;
			var original = cache;
			for (var move = 1; move <= 20; move++) {
				var moved = xf;
				moved.Position.X += FP.FromRatio(move, 20).To64();
				var expected = BoxSatCacheReference.Collide(box, Pose(), box, moved, original, out _, out var reason);
				if (reason is not (BoxSatCacheReference.Rejection.Segments or BoxSatCacheReference.Rejection.Drift))
					continue;
				Assert.That(expected, Is.EqualTo(BoxSatResult.FullSearch));
				cache = original;
				var m = Manifold.Collide(box, Pose(), box, moved, ref cache, out var result);
				Assert.That(result, Is.EqualTo(BoxSatResult.FullSearch));
				Assert.That(m, Is.EqualTo(BoxSatReference.Collide(box, Pose(), box, moved)));
				if (reason == BoxSatCacheReference.Rejection.Segments)
					segmentRejects++;
				else
					driftRejects++;
			}
		}
		Assert.That(segmentRejects, Is.GreaterThan(0));
		Assert.That(driftRejects, Is.GreaterThan(0));
	}

	[Test]
	public void GeometryMutationRemovalAndEntityReuseStartWithInvalidCache() {
		CreateWorld<BoxCacheLifecycleWorld>(Scene());
		Tick<BoxCacheLifecycleWorld>();
		var contactGid = Contacts<BoxCacheLifecycleWorld>().Single().Gid;
		Assert.That(Contacts<BoxCacheLifecycleWorld>().Single().Cache.Axis, Is.Not.EqualTo(BoxSatAxis.Invalid));
		EntityGID dynamicShape = default;
		foreach (var e in C.W.Query<All<Shape, C.W.Link<BodyOwner>>>().Entities()) {
			if (e.Read<Shape>().Type == ShapeType.Hull && e.Read<C.W.Link<BodyOwner>>().Value.TryUnpack<BoxCacheLifecycleWorld>(out var body) && body.Read<Body>().Type == BodyType.Dynamic)
				dynamicShape = e.GID;
		}
		Assert.That(dynamicShape.TryUnpack<BoxCacheLifecycleWorld>(out var shape), Is.True);
		C.ShapeOperations.SetBox(shape, FVector3.Zero, new FVector3(.55.ToFP(), FP.Half, FP.Half));
		Assert.That(contactGid.TryUnpack<BoxCacheLifecycleWorld>(out _), Is.False);
		// Pair creation occurs before narrowphase. Inspect this boundary explicitly.
		new C.ShapeProxySystem().Update();
		C.W.GetResource<C.BroadPhase>().UpdatePairs(C.ContactSystem.TryCreateContact);
		var created = Contacts<BoxCacheLifecycleWorld>().Single();
		Assert.That(Bytes(created.Cache), Is.EqualTo(Bytes(default(BoxSatCache))));
		new C.ContactSystem().Update();
		Assert.That(Contacts<BoxCacheLifecycleWorld>().Single().Cache.Axis, Is.Not.EqualTo(BoxSatAxis.Invalid));
		Assert.That(shape.Read<C.W.Link<BodyOwner>>().Value.TryUnpack<BoxCacheLifecycleWorld>(out var owner), Is.True);
		C.BodyOperations.SetEnabled(owner, false);
		Assert.That(Contacts<BoxCacheLifecycleWorld>(), Is.Empty);
		C.BodyOperations.SetEnabled(owner, true);
		new C.ShapeProxySystem().Update();
		C.W.GetResource<C.BroadPhase>().UpdatePairs(C.ContactSystem.TryCreateContact);
		Assert.That(Bytes(Contacts<BoxCacheLifecycleWorld>().Single().Cache), Is.EqualTo(Bytes(default(BoxSatCache))));
		C.BodyOperations.DestroyBody(owner);
		Assert.That(Contacts<BoxCacheLifecycleWorld>(), Is.Empty);
	}

	[Test]
	public void RollbackAndDistinctWorldFullSyncRestoreCacheAndEveryTickHash() {
		CreateWorld<BoxCacheReplayWorld>(Scene());
		for (var i = 0; i < 60; i++)
			Tick<BoxCacheReplayWorld>();
		var before = Contacts<BoxCacheReplayWorld>();
		Assert.That(before.Any(c => c.Cache.Axis != BoxSatAxis.Invalid), Is.True);
		var rollback = new Core<BoxCacheReplayWorld>.GameWorldRollback(2);
		rollback.SaveFrame();
		var writer = SnapshotWriter<BoxCacheReplayWorld>();
		var originalHash = WorldHash<BoxCacheReplayWorld>(ref writer);
		var hashes = new ulong[180];
		for (var i = 0; i < hashes.Length; i++) {
			if (i == 20)
				Push<BoxCacheReplayWorld>();
			Tick<BoxCacheReplayWorld>();
			hashes[i] = WorldHash<BoxCacheReplayWorld>(ref writer);
		}
		rollback.SaveFrame();
		rollback.Rollback(1);
		Assert.That(Contacts<BoxCacheReplayWorld>().Select(c => Bytes((c.Gid.Raw, c.Cache))), Is.EqualTo(before.Select(c => Bytes((c.Gid.Raw, c.Cache)))), "all inline cache bytes and GIDs restore");
		Assert.That(WorldHash<BoxCacheReplayWorld>(ref writer), Is.EqualTo(originalHash));
		CreateWorld<BoxCacheSyncWorld>(new LevelData([]));
		var sync = SnapshotWriter<BoxCacheReplayWorld>();
		new Core<BoxCacheReplayWorld>.GameWorldFullSyncHandler().WriteFullSync(ref sync);
		var reader = sync.AsReader();
		new Core<BoxCacheSyncWorld>.GameWorldFullSyncHandler().ReadFullSync(ref reader);
		Assert.That(Contacts<BoxCacheSyncWorld>().Select(c => Bytes((c.Gid.Raw, c.Cache))), Is.EqualTo(before.Select(c => Bytes((c.Gid.Raw, c.Cache)))));
		var otherWriter = SnapshotWriter<BoxCacheSyncWorld>();
		for (var i = 0; i < hashes.Length; i++) {
			if (i == 20) { Push<BoxCacheReplayWorld>(); Push<BoxCacheSyncWorld>(); }
			Tick<BoxCacheReplayWorld>();
			Tick<BoxCacheSyncWorld>();
			Assert.That(WorldHash<BoxCacheReplayWorld>(ref writer), Is.EqualTo(hashes[i]), $"rollback tick {i}");
			Assert.That(WorldHash<BoxCacheSyncWorld>(ref otherWriter), Is.EqualTo(hashes[i]), $"full-sync tick {i}");
		}
		TestContext.WriteLine($"STATE-HASH box-cache-replay {hashes[^1]:x16}");
	}

	private static void Tick<T>() where T : struct, IWorldType, ISessionType {
		World<T>.Tick();
		new Core<T>.ShapeProxySystem().Update();
		new Core<T>.ContactSystem().Update();
		new Core<T>.ContactSolverSystem().Update();
	}
	private static void Push<T>() where T : struct, IWorldType, ISessionType {
		foreach (var e in World<T>.Query<All<Body>>().Entities()) {
			if (e.Read<Body>().Type == BodyType.Dynamic)
				Core<T>.BodyOperations.SetLinearVelocity(e, new FVector3(FP.One, FP.Zero, FP.Zero));
		}
	}
	private static (EntityGID Gid, BoxSatCache Cache)[] Contacts<T>() where T : struct, IWorldType {
		var contacts = new List<(EntityGID, BoxSatCache)>();
		foreach (var e in World<T>.Query<All<Contact>>().Entities())
			contacts.Add((e.GID, e.Read<Contact>().SatCache));
		return contacts.ToArray();
	}
	private static LevelData Scene() => new([
		new EntityPlacement("Ground", LevelEntityType.StaticGeometry, Pose(x: 100), new PlacementComponents(Body: BodyType.Static,
			BoxShape: new BoxShapeData(new FVector3(40.ToFP(), FP.Half, 40.ToFP()), FP.One), Navigation: NavContribution.Walkable)),
		new EntityPlacement("Crate", LevelEntityType.Crate, Pose(x: 100, y: .99), new PlacementComponents(Body: BodyType.Dynamic,
			Health: 100, Loot: LootKind.None, BoxShape: new BoxShapeData(new FVector3(FP.Half, FP.Half, FP.Half), FP.One), View: ViewAsset.Crate))]);
}
