using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

public static partial class Program {
	/// <summary>
	/// Exercises the new world-level ray cast API (PhysicsQueries.CastRay/CastRayClosest via
	/// BroadPhase.CastRay's shared-shrinking-maxFraction traversal across all three trees) against
	/// all three shape types, including Hull.RayCast (previously missing entirely) under a
	/// non-trivial rotation to prove the local/world transform round-trip is correct, not just the
	/// axis-aligned case.
	/// </summary>
	private static void RayCastHitsSphereCapsuleAndBoxTest() {
		Console.WriteLine("--- RayCastHitsSphereCapsuleAndBoxTest ---");
		Bootstrap();

		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		ShapeFactory.CreateShape(sphereBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(2, 1)));

		var capsuleBody = W.NewEntity<Default>();
		capsuleBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(20, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(capsuleBody, Shape.MakeCapsule(new FVector3(-FP.FromRatio(2, 1), FP.Zero, FP.Zero), new FVector3(FP.FromRatio(2, 1), FP.Zero, FP.Zero), FP.One));

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(40, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity) });
		ShapeFactory.CreateShape(boxBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(2, 1), FP.FromRatio(2, 1), FP.FromRatio(2, 1))));

		// Half-extents (1, 2, 3) rotated 90 degrees around Z: the local +X face (extent 1) becomes
		// the world "up" face instead of the unrotated +Y face (extent 2) -- if the rotation weren't
		// correctly applied to both the incoming ray and the outgoing hit point/normal, this would
		// either miss entirely or report the wrong height/normal.
		var rotatedBoxBody = W.NewEntity<Default>();
		var rotation = FQuaternion.AxisAngleDegrees(new FVector3(FP.Zero, FP.Zero, FP.One), FP.FromRatio(90, 1));
		rotatedBoxBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(60, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), rotation) });
		ShapeFactory.CreateShape(rotatedBoxBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.FromRatio(2, 1), FP.FromRatio(3, 1))));

		// ShapeProxySystem only creates broad-phase proxies during Systems.Update() -- without at
		// least one tick, none of the shapes above are in the broad phase yet and every cast below
		// would miss regardless of whether the ray math itself is correct.
		W.Tick();
		Systems.Update();

		bool CastDown(int x, out RayCastResult result) {
			var origin = new FPos(Fixed64.FP.FromRatio(x, 1), Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero);
			var translation = new FVector3(FP.Zero, -FP.FromRatio(20, 1), FP.Zero);
			return PhysicsQueries.CastRayClosest(W.GetResource<BroadPhase>(), origin, translation, Filter.Default, out result);
		}

		Check("ray hits the sphere", CastDown(0, out var sphereHit));
		Check("sphere hit lands at the top of the sphere (y~2)", Math.Abs(Fixed64.FConversions.ToDouble(sphereHit.Point.Y) - 2.0) < 0.05);
		Check("sphere hit normal points up", sphereHit.Normal.Y.ToDouble() > 0.9);

		Check("ray hits the capsule", CastDown(20, out var capsuleHit));
		Check("capsule hit lands at the top of the capsule (y~1)", Math.Abs(Fixed64.FConversions.ToDouble(capsuleHit.Point.Y) - 1.0) < 0.05);
		Check("capsule hit normal points up", capsuleHit.Normal.Y.ToDouble() > 0.9);

		Check("ray hits the axis-aligned box", CastDown(40, out var boxHit));
		Check("box hit lands at the top of the box (y~2)", Math.Abs(Fixed64.FConversions.ToDouble(boxHit.Point.Y) - 2.0) < 0.05);
		Check("box hit normal points up", boxHit.Normal.Y.ToDouble() > 0.9);

		Check("ray hits the rotated box", CastDown(60, out var rotatedHit));
		Check("rotated box hit lands at the rotated top face (y~1, not the unrotated y~2)", Math.Abs(Fixed64.FConversions.ToDouble(rotatedHit.Point.Y) - 1.0) < 0.05);
		Check("rotated box hit normal points up", rotatedHit.Normal.Y.ToDouble() > 0.9);

		Shutdown();
	}

	/// <summary>
	/// Verifies the negative cases the 5-way contract depends on: a genuine miss, a sensor being
	/// skipped, and Filter excluding a shape -- each backed by a positive control proving the fixture
	/// is live (proxies exist, the shape is hittable), so the misses can't pass vacuously the way
	/// they did before this test ever ticked ShapeProxySystem.
	/// </summary>
	private static void RayCastMissAndFilterTest() {
		Console.WriteLine("--- RayCastMissAndFilterTest ---");
		Bootstrap();

		var broadPhase = W.GetResource<BroadPhase>();

		Check("ray into empty space misses", !PhysicsQueries.CastRayClosest(broadPhase, new FPos(Fixed64.FP.FromRatio(500, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), new FVector3(FP.Zero, -FP.One, FP.Zero), Filter.Default, out _));

		var sensorBody = W.NewEntity<Default>();
		sensorBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		var sensorShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(2, 1));
		sensorShape.IsSensor = true;
		ShapeFactory.CreateShape(sensorBody, sensorShape);

		var filteredBody = W.NewEntity<Default>();
		filteredBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(100, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity) });
		var filteredShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(2, 1));
		filteredShape.Filter = new Filter { CategoryBits = 2, MaskBits = ulong.MaxValue, GroupIndex = 0 };
		var filteredShapeEntity = ShapeFactory.CreateShape(filteredBody, filteredShape);

		// The original version of this test cast before ShapeProxySystem ever ran, so neither shape
		// had a broad-phase proxy and the "sensor skipped"/"filter excluded" checks passed vacuously.
		W.Tick();
		Systems.Update();

		var counts = PhysicsDiagnostics.Capture();
		Check("both fixture shapes have broad-phase proxies before querying", counts.Shapes == counts.Proxies && counts.Proxies >= 2);

		var origin = new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero);
		var translation = new FVector3(FP.Zero, -FP.FromRatio(20, 1), FP.Zero);
		Check("ray skips a sensor shape", !PhysicsQueries.CastRayClosest(broadPhase, origin, translation, Filter.Default, out _));

		var filteredOrigin = new FPos(Fixed64.FP.FromRatio(100, 1), Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero);
		Check("positive control: the category-2 sphere is hittable with an allowing query filter", PhysicsQueries.CastRayClosest(broadPhase, filteredOrigin, translation, Filter.Default, out var allowedHit) && allowedHit.Shape == filteredShapeEntity.GID);

		var queryFilter = new Filter { CategoryBits = ulong.MaxValue, MaskBits = 1, GroupIndex = 0 };
		Check("ray respects Filter.ShouldCollide", !PhysicsQueries.CastRayClosest(broadPhase, filteredOrigin, translation, queryFilter, out _));

		Shutdown();
	}

	private static void HollowSphereRayFractionTest() {
		Console.WriteLine("--- HollowSphereRayFractionTest ---");
		var sphere = new Sphere(FVector3.Zero, 2.ToFP());
		var outside = Sphere.RayCastHollow(sphere, new RayCastInput {
			Origin = new FVector3(-10.ToFP(), FP.Zero, FP.Zero),
			Translation = new FVector3(20.ToFP(), FP.Zero, FP.Zero),
			MaxFraction = FP.One,
		});
		Check("hollow-sphere ray returns a normalized translation fraction", outside.Hit && FP.Abs(outside.Fraction - FP.FromRatio(2, 5)) < FP.FromRatio(1, 1000));

		var inside = Sphere.RayCastHollow(sphere, new RayCastInput {
			Origin = FVector3.Zero,
			Translation = new FVector3(8.ToFP(), FP.Zero, FP.Zero),
			MaxFraction = FP.One,
		});
		Check("hollow-sphere ray starting inside hits the far wall", inside.Hit && FP.Abs(inside.Fraction - FP.Quarter) < FP.FromRatio(1, 1000));
		Check("zero-length hollow-sphere ray is a miss", !Sphere.RayCastHollow(sphere, new RayCastInput { Origin = FVector3.Zero, MaxFraction = FP.One }).Hit);
	}

	private static void WorldShapeQueriesTest() {
		Console.WriteLine("--- WorldShapeQueriesTest ---");
		Bootstrap();
		var solidBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(solidBody, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var solidShape = ShapeFactory.CreateShape(solidBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.Quarter, 2.ToFP(), 2.ToFP())));
		var solidShapeGid = solidShape.GID;

		var sensorBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(sensorBody, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var sensor = Shape.MakeSphere(FVector3.Zero, FP.One);
		sensor.IsSensor = true;
		var sensorShape = ShapeFactory.CreateShape(sensorBody, sensor);
		var sensorShapeGid = sensorShape.GID;

		W.Tick();
		Systems.Update();
		var broadPhase = W.GetResource<BroadPhase>();
		var sphereProxy = new ShapeProxy(new[] { FVector3.Zero }, FP.Half);
		var queryXf = new FWorldTransform(FPos.Zero, FQuaternion.Identity);
		Check("world shape cast hits a thin box", PhysicsQueries.CastShapeClosest(broadPhase, queryXf, sphereProxy, new FVector3(8.ToFP(), FP.Zero, FP.Zero), Filter.Default, QuerySensorMode.Exclude, out var castHit)
			&& castHit.Shape == solidShapeGid && castHit.Fraction > FP.Zero && castHit.Fraction < FP.One);

		var overlapCount = 0;
		PhysicsQueries.OverlapShape(broadPhase,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			sphereProxy, Filter.Default, QuerySensorMode.Exclude, gid => {
				overlapCount++;
				return gid != solidShapeGid;
			});
		Check("world overlap performs an exact convex test and reports the solid", overlapCount == 1);

		var sensorRayOrigin = new FPos(Fixed64.FP.FromRatio(8, 1), Fixed64.FP.Zero, Fixed64.FP.Zero);
		var sensorRay = new FVector3(4.ToFP(), FP.Zero, FP.Zero);
		Check("world ray excludes sensors by default", !PhysicsQueries.CastRayClosest(broadPhase, sensorRayOrigin, sensorRay, Filter.Default, out _));
		Check("world ray can include sensors", PhysicsQueries.CastRayClosest(broadPhase, sensorRayOrigin, sensorRay, Filter.Default, QuerySensorMode.Include, out var sensorHit)
			&& sensorHit.Shape == sensorShapeGid);
		Check("world shape cast excludes sensors", !PhysicsQueries.CastShapeClosest(broadPhase,
			new FWorldTransform(sensorRayOrigin, FQuaternion.Identity), sphereProxy, sensorRay, Filter.Default, QuerySensorMode.Exclude, out _));
		Check("world shape cast can include sensors", PhysicsQueries.CastShapeClosest(broadPhase,
			new FWorldTransform(sensorRayOrigin, FQuaternion.Identity), sphereProxy, sensorRay, Filter.Default, QuerySensorMode.Include, out var sensorCastHit)
			&& sensorCastHit.Shape == sensorShapeGid);

		var sensorOverlapExcluded = 0;
		PhysicsQueries.OverlapShape(broadPhase, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			sphereProxy, Filter.Default, QuerySensorMode.Exclude, _ => { sensorOverlapExcluded++; return true; });
		var sensorOverlapIncluded = 0;
		PhysicsQueries.OverlapShape(broadPhase, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			sphereProxy, Filter.Default, QuerySensorMode.Include, _ => { sensorOverlapIncluded++; return true; });
		Check("world overlap sensor inclusion is configurable", sensorOverlapExcluded == 0 && sensorOverlapIncluded == 1);

		ShapeOperations.SetFilter(solidShape, new Filter { CategoryBits = 2, MaskBits = 0, GroupIndex = 5 });
		var positiveGroupHits = 0;
		PhysicsQueries.OverlapShape(broadPhase,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity), sphereProxy,
			new Filter { CategoryBits = 4, MaskBits = 0, GroupIndex = 5 }, QuerySensorMode.Exclude, _ => { positiveGroupHits++; return true; });
		var negativeGroupHits = 0;
		PhysicsQueries.OverlapShape(broadPhase,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity), sphereProxy,
			new Filter { CategoryBits = 4, MaskBits = ulong.MaxValue, GroupIndex = -5 }, QuerySensorMode.Exclude, _ => { negativeGroupHits++; return true; });
		Check("world queries honor positive and negative collision groups", positiveGroupHits == 1 && negativeGroupHits == 0);
		Shutdown();
	}

	private static void WorldQueryCallbackContractTest() {
		Console.WriteLine("--- WorldQueryCallbackContractTest ---");
		Bootstrap();
		var farBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(farBody, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(6, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var farShape = ShapeFactory.CreateShape(farBody, Shape.MakeSphere(FVector3.Zero, FP.Half));
		var nearBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(nearBody, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(2, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var nearShape = ShapeFactory.CreateShape(nearBody, Shape.MakeSphere(FVector3.Zero, FP.Half));
		var lastBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(lastBody, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(8, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(lastBody, Shape.MakeSphere(FVector3.Zero, FP.Half));
		W.Tick();
		Systems.Update();

		var broadPhase = W.GetResource<BroadPhase>();
		var origin = FPos.Zero;
		var translation = new FVector3(10.ToFP(), FP.Zero, FP.Zero);
		var order = new List<EntityGID>();
		PhysicsQueries.CastRay(broadPhase, origin, translation, Filter.Default, (EntityGID gid, in FPos _, in FVector3 _, FP _) => {
			order.Add(gid);
			return FP.One;
		});
		Check("ray callbacks use stable shape GID order", order.Count == 3 && order[0] == farShape.GID && order[1] == nearShape.GID);

		var terminatedCount = 0;
		PhysicsQueries.CastRay(broadPhase, origin, translation, Filter.Default, (EntityGID _, in FPos _, in FVector3 _, FP _) => {
			terminatedCount++;
			return FP.Zero;
		});
		Check("ray callback zero terminates traversal", terminatedCount == 1);

		var clippedCount = 0;
		PhysicsQueries.CastRay(broadPhase, origin, translation, Filter.Default, (EntityGID _, in FPos _, in FVector3 _, FP fraction) => {
			clippedCount++;
			return fraction;
		});
		Check("ray callback fractions clip farther candidates", clippedCount == 2);

		var ignoredCount = 0;
		PhysicsQueries.CastRay(broadPhase, origin, translation, Filter.Default, (EntityGID _, in FPos _, in FVector3 _, FP _) => {
			ignoredCount++;
			return -FP.One;
		});
		Check("negative ray callback values ignore without clipping", ignoredCount == 3);

		var shapeCastOrder = new List<EntityGID>();
		var proxy = new ShapeProxy(new[] { FVector3.Zero }, FP.FromRatio(1, 10));
		PhysicsQueries.CastShape(broadPhase, FWorldTransform.Identity, proxy, translation, Filter.Default, QuerySensorMode.Exclude,
			(EntityGID gid, in FPos _, in FVector3 _, FP _) => { shapeCastOrder.Add(gid); return FP.One; });
		Check("shape-cast callbacks use stable shape GID order", shapeCastOrder.Count == 3 && shapeCastOrder[0] == farShape.GID && shapeCastOrder[1] == nearShape.GID);

		var shapeTerminatedCount = 0;
		PhysicsQueries.CastShape(broadPhase, FWorldTransform.Identity, proxy, translation, Filter.Default, QuerySensorMode.Exclude,
			(EntityGID _, in FPos _, in FVector3 _, FP _) => { shapeTerminatedCount++; return FP.Zero; });
		var shapeIgnoredCount = 0;
		PhysicsQueries.CastShape(broadPhase, FWorldTransform.Identity, proxy, translation, Filter.Default, QuerySensorMode.Exclude,
			(EntityGID _, in FPos _, in FVector3 _, FP _) => { shapeIgnoredCount++; return -FP.One; });
		var shapeClippedCount = 0;
		PhysicsQueries.CastShape(broadPhase, FWorldTransform.Identity, proxy, translation, Filter.Default, QuerySensorMode.Exclude,
			(EntityGID _, in FPos _, in FVector3 _, FP fraction) => { shapeClippedCount++; return fraction; });
		Check("shape-cast callbacks honor terminate, ignore, and clip semantics",
			shapeTerminatedCount == 1 && shapeIgnoredCount == 3 && shapeClippedCount == 2);

		var overlapTerminatedCount = 0;
		PhysicsQueries.OverlapShape(broadPhase,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			new ShapeProxy(new[] { FVector3.Zero }, 5.ToFP()), Filter.Default, QuerySensorMode.Exclude,
			_ => { overlapTerminatedCount++; return false; });
		Check("overlap callback false terminates traversal", overlapTerminatedCount == 1);
		Shutdown();
	}

}
