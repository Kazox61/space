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

public struct TestWorld : IWorldType, ISessionType { }
public struct SyncTargetWorld : IWorldType, ISessionType { }

/// <summary>Mirrors GameWorldPrev/WP: a second parallel world the client deserializes into every simulated tick for render interpolation (GameInterpolationReceiver.SaveInterpolationState).</summary>
public struct TestWorldPrev : IWorldType { }

public abstract class WPrev : World<TestWorldPrev> { }

public static partial class Program {
	private static int _failures;
	private const int TickRate = 60;

	public static void Main(string[] args) {
		if (args.Length > 0 && args[0] == "visual") {
			VisualTest.Run();
			return;
		}

		if (args.Length > 0 && args[0] == "bench") {
			var enforce = Array.IndexOf(args, "--enforce") >= 0;
			if (!enforce) {
				BenchResimulationCost();
				BenchInterpolationSnapshotCost();
			}
			if (!BenchPhysicsBudgets() && enforce) {
				Console.WriteLine("PHYSICS BUDGET EXCEEDED");
				Environment.Exit(1);
			}
			return;
		}

		TouchEventsTest();
		ContactPersistenceTest();
		ParallelCapsuleManifoldTest();
		FeatureIdValidityTest();
		ParallelCapsuleRestingStabilityTest();
		SpeculativeContactAndEventFlagsTest();
		ContactHitEventTest();
		DropAndRestTest();
		CapsuleOnSphereSmokeTest();
		BoxOnSphereSmokeTest();
		BoxOnBoxSmokeTest();
		BoxOnCapsuleSmokeTest();
		OverlappingSpawnStressTest();
		RollbackRoundTripTest();
		RollbackStateHashReplayTest();
		KinematicBodyDrivenByVelocityPushesDynamicBodyTest();
		MoverPushesRestingSphereStablyTest();
		MoverBlockedByStaticWallTest();
		MoverStressTestAgainstRealGroundSize();
		RollingResistanceBringsPushedSphereToRestTest();
		MoverFallsLandsAndJumpsTest();
		SensorDoesNotProduceCollisionResponseTest();
		SensorSensorDirectionalEventsTest();
		RayCastHitsSphereCapsuleAndBoxTest();
		RayCastMissAndFilterTest();
		HollowSphereRayFractionTest();
		WorldShapeQueriesTest();
		WorldQueryCallbackContractTest();
		CharacterMoverFilterTest();
		RotatingTimeOfImpactTest();
		AutomaticFastBodyCcdTest();
		AngularCcdTest();
		BulletCcdTargetPolicyTest();
		MaximumSpeedBulletCcdTest();
		BulletCcdRollbackTest();
		PogoGroundingRayTest();
		KinematicProjectileKillsDummyTest();
		RejectedBroadPhasePairsTest();
		BodyTransformMutationTest();
		StaticTransformMutationTest();
		BodyTypeAndEnabledMutationTest();
		ShapeFilterMutationTest();
		ShapeGeometryDensityAndForcesTest();
		MutationRollbackTest();
		ProjectileDespawnTtlTest();
		SessionInputDuplicateRetryTest();
		BodyDestructionRollbackTest();
		DeathSystemPhysicsLifecycleTest();
		ProjectileLifecycleCountsTest();
		FixedPointBoundaryValidationTest();
		EscapingBodyIsDisabledTest();
		PhysicsConfigurationRollbackTest();
		CrossWorldFullSyncTest();
		RunPhase6Tests();

		if (_failures == 0) {
			Console.WriteLine("ALL CHECKS PASSED");
		} else {
			Console.WriteLine($"{_failures} CHECK(S) FAILED");
			Environment.Exit(1);
		}
	}

	internal static void Bootstrap() {
		W.Create(GameWorldSetup.WorldConfig);
		Systems.Create(snapshotGuid: GameSystemsSnapshotGuid);
		W.Types().RegisterAll(typeof(CoreRoot).Assembly);
		W.SetResource(new PhysicsWorld());
		W.SetResource(new BroadPhase());
		Systems.Add(new DamageSystem(), order: 0);
		Systems.Add(new ProjectileDespawnSystem(), order: 0);
		Systems.Add(new DeathSystem(), order: 1);
		Systems.Add(new ShapeProxySystem(), order: 2);
		Systems.Add(new ContactSystem(), order: 3);
		Systems.Add(new ContactSolverSystem(), order: 4);
		Systems.Add(new BodyTransformSyncSystem(), order: 5);
		Systems.Add(new ProjectileHitSystem(), order: 6);
		W.Initialize();
		Systems.Initialize();

		Space.GameCore.Const.DeltaTime = Fixed64.FP.One / TickRate;
		Space.GameCore.Const.InvDeltaTime = Fixed64.FP.FromRatio(TickRate, 1);
	}

	internal static void Shutdown() {
		Systems.Destroy();
		W.Destroy();
	}

	private static void TouchEventsTest() {
		Console.WriteLine("--- TouchEventsTest ---");
		Bootstrap();

		var beginReceiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();
		var endReceiver = W.RegisterEventReceiver<ContactEndTouchEvent>();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		var groundShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(5, 1));
		groundShape.EnableContactEvents = true;
		ShapeFactory.CreateShape(groundBody, groundShape);

		var fallingBody = W.NewEntity<Default>();
		fallingBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(7, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var fallingShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(3, 1));
		fallingShape.EnableContactEvents = true;
		ShapeFactory.CreateShape(fallingBody, fallingShape);

		W.Tick();
		Systems.Update();

		Check("ContactBeginTouchEvent fires when overlapping", beginReceiver.ReadAll(static _ => { }) == 1);
		Check("ContactEndTouchEvent does not fire yet", endReceiver.ReadAll(static _ => { }) == 0);

		BodyOperations.SetTransform(fallingBody,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(500, 1), Fixed64.FP.Zero), FQuaternion.Identity));

		W.Tick();
		Systems.Update();

		Check("ContactEndTouchEvent fires after separation", endReceiver.ReadAll(static _ => { }) == 1);

		Shutdown();
	}

	private static void ContactPersistenceTest() {
		Console.WriteLine("--- ContactPersistenceTest ---");
		Bootstrap();

		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(ground, Shape.MakeBox(FVector3.Zero, new FVector3(4.ToFP(), FP.Half, 4.ToFP())));
		var boxes = new List<W.Entity>();
		for (var i = 0; i < 3; i++) {
			var box = W.NewEntity<Default>();
			BodyOperations.CreateBody(box, BodyType.Dynamic,
				new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3 + 2 * i, 1), Fixed64.FP.Zero), FQuaternion.Identity));
			var boxShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
			boxShape.Density = FP.One;
			ShapeFactory.CreateShape(box, boxShape);
			boxes.Add(box);
		}

		for (var i = 0; i < 360; i++) {
			W.Tick();
			Systems.Update();
		}

		var contactEntity = default(W.Entity);
		foreach (var entity in W.Query<All<Contact>>().Entities()) {
			contactEntity = entity;
			break;
		}
		var oldImpulses = new Dictionary<uint, FP>();
		if (contactEntity.Has<Contact>()) {
			ref readonly var manifold = ref contactEntity.Read<Contact>().Manifold;
			for (var i = 0; i < manifold.PointCount; i++) {
				var point = manifold.GetPoint(i);
				oldImpulses[point.FeatureId] = point.NormalImpulse;
			}
		}
		W.Tick();
		Systems.Update();
		var persisted = false;
		var matchedPriorImpulse = false;
		ref readonly var updatedManifold = ref contactEntity.Read<Contact>().Manifold;
		for (var i = 0; i < updatedManifold.PointCount; i++) {
			var point = updatedManifold.GetPoint(i);
			persisted |= point.Persisted;
			matchedPriorImpulse |= point.Persisted && oldImpulses.TryGetValue(point.FeatureId, out var oldImpulse) && oldImpulse > FP.Zero;
		}
		Check("resting manifold points retain stable feature identities", persisted);
		Check("persisted feature IDs carry a prior nonzero normal impulse", matchedPriorImpulse);
		var stackStable = true;
		for (var i = 0; i < boxes.Count; i++) {
			var y = Fixed64.FConversions.ToDouble(boxes[i].Read<Body>().Transform.Position.Y);
			stackStable &= Math.Abs(y - (1.5 + 2 * i)) < 0.1;
		}
		Check("long-running box stack remains stable without drift", stackStable);
		Shutdown();
	}

	private static void ParallelCapsuleManifoldTest() {
		Console.WriteLine("--- ParallelCapsuleManifoldTest ---");
		var shapeA = Shape.MakeCapsule(new FVector3(-2.ToFP(), FP.Zero, FP.Zero), new FVector3(2.ToFP(), FP.Zero, FP.Zero), FP.One);
		var shapeB = Shape.MakeCapsule(new FVector3(-2.ToFP(), FP.Zero, FP.Zero), new FVector3(2.ToFP(), FP.Zero, FP.Zero), FP.One);
		var manifold = Manifold.Collide(shapeA, new FWorldTransform(FPos.Zero, FQuaternion.Identity), shapeB,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3, 2), Fixed64.FP.Zero), FQuaternion.Identity));
		Check("near-parallel capsules produce a two-point manifold", manifold.PointCount == 2);
		Check("parallel capsule points have distinct stable feature IDs",
			manifold.PointCount == 2 && manifold.Point0.FeatureId != manifold.Point1.FeatureId);
		var coincident = Manifold.Collide(shapeA, new FWorldTransform(FPos.Zero, FQuaternion.Identity), shapeB,
			new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		Check("deeply overlapping capsule cores retain a fallback contact", coincident.PointCount == 1 && coincident.Point0.Separation < FP.Zero);
	}

	private static void FeatureIdValidityTest() {
		Console.WriteLine("--- FeatureIdValidityTest ---");
		var sphere = Shape.MakeSphere(FVector3.Zero, FP.One);
		var sphereManifold = Manifold.Collide(sphere, new FWorldTransform(FPos.Zero, FQuaternion.Identity), sphere,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity));
		Check("analytic sphere contacts have no geometric feature ID",
			sphereManifold.PointCount == 1 && !sphereManifold.Point0.HasFeatureId);

		var capsule = Shape.MakeCapsule(new FVector3(-2.ToFP(), FP.Zero, FP.Zero), new FVector3(2.ToFP(), FP.Zero, FP.Zero), FP.One);
		var capsuleManifold = Manifold.Collide(capsule, new FWorldTransform(FPos.Zero, FQuaternion.Identity), capsule,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3, 2), Fixed64.FP.Zero), FQuaternion.Identity));
		Check("zero-valued geometric feature IDs remain valid",
			capsuleManifold.PointCount == 2 && capsuleManifold.Point0.FeatureId == 0 && capsuleManifold.Point0.HasFeatureId);

		Bootstrap();
		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(staticBody, sphere);
		var dynamicBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(dynamicBody, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(dynamicBody, sphere);

		W.Tick();
		Systems.Update();
		W.Tick();
		Systems.Update();

		var falselyPersisted = false;
		foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
			ref readonly var manifold = ref contactEntity.Read<Contact>().Manifold;
			for (var i = 0; i < manifold.PointCount; i++) {
				falselyPersisted |= manifold.GetPoint(i).Persisted;
			}
		}
		Check("contacts without geometric feature IDs are not warm-start matched", !falselyPersisted);
		Shutdown();
	}

	private static void ParallelCapsuleRestingStabilityTest() {
		Console.WriteLine("--- ParallelCapsuleRestingStabilityTest ---");
		Bootstrap();
		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(ground,
			Shape.MakeCapsule(new FVector3(-3.ToFP(), FP.Zero, FP.Zero), new FVector3(3.ToFP(), FP.Zero, FP.Zero), FP.One));
		var bodies = new List<W.Entity>();
		for (var i = 0; i < 3; i++) {
			var body = W.NewEntity<Default>();
			BodyOperations.CreateBody(body, BodyType.Dynamic,
				new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(4 + 2 * i, 1), Fixed64.FP.Zero), FQuaternion.Identity));
			var shape = Shape.MakeCapsule(new FVector3(-2.ToFP(), FP.Zero, FP.Zero), new FVector3(2.ToFP(), FP.Zero, FP.Zero), FP.One);
			shape.Density = FP.One;
			ShapeFactory.CreateShape(body, shape);
			bodies.Add(body);
		}

		for (var i = 0; i < 360; i++) {
			W.Tick();
			Systems.Update();
		}
		var stackStable = true;
		var settled = true;
		for (var i = 0; i < bodies.Count; i++) {
			var finalY = Fixed64.FConversions.ToDouble(bodies[i].Read<Body>().Transform.Position.Y);
			stackStable &= Math.Abs(finalY - (2.0 + 2 * i)) < 0.1;
			settled &= FVector3.Length(bodies[i].Read<Body>().LinearVelocity).ToDouble() < 0.05;
		}
		Check("parallel capsules remain stacked at their expected resting heights", stackStable);
		Check("parallel capsule stack settles without long-running jitter", settled);
		Shutdown();
	}

	private static void SpeculativeContactAndEventFlagsTest() {
		Console.WriteLine("--- SpeculativeContactAndEventFlagsTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;
		var receiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();
		var endReceiver = W.RegisterEventReceiver<ContactEndTouchEvent>();

		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var staticShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		staticShape.EnableContactEvents = true;
		var staticShapeEntity = ShapeFactory.CreateShape(staticBody, staticShape);
		var dynamicBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(dynamicBody, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(201, 100), Fixed64.FP.Zero), FQuaternion.Identity));
		var dynamicShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		dynamicShape.Density = FP.One;
		dynamicShape.EnableContactEvents = true;
		var dynamicShapeEntity = ShapeFactory.CreateShape(dynamicBody, dynamicShape);

		W.Tick();
		Systems.Update();
		var speculativeOnly = false;
		foreach (var entity in W.Query<All<Contact>>().Entities()) {
			ref readonly var contact = ref entity.Read<Contact>();
			speculativeOnly = contact.Manifold.PointCount > 0 && !contact.Touching && contact.Manifold.MinSeparation() > FP.Zero;
		}
		Check("positive-separation manifolds remain speculative rather than touching", speculativeOnly);
		Check("speculative contacts do not emit begin-touch events", receiver.ReadAll(static _ => { }) == 0);

		BodyOperations.SetTransform(dynamicBody,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(199, 100), Fixed64.FP.Zero), FQuaternion.Identity));
		W.Tick();
		Systems.Update();
		Check("crossing into physical overlap emits one begin-touch event", receiver.ReadAll(static _ => { }) == 1);
		var existingContact = default(EntityGID);
		foreach (var contact in W.Query<All<Contact>>().Entities()) {
			if (contact.Read<Contact>().ShapeA == dynamicShapeEntity.GID || contact.Read<Contact>().ShapeB == dynamicShapeEntity.GID) {
				existingContact = contact.GID;
				break;
			}
		}
		ShapeOperations.SetEventFlags(dynamicShapeEntity, contactEvents: true, sensorEvents: false, hitEvents: true);
		W.Tick();
		Systems.Update();
		Check("changing hit-event policy retains a touching contact", existingContact.TryUnpack<TestWorld>(out _));
		Check("changing hit-event policy does not synthesize end/begin transitions",
			endReceiver.ReadAll(static _ => { }) == 0 && receiver.ReadAll(static _ => { }) == 0);
		ShapeOperations.SetEventFlags(staticShapeEntity, contactEvents: false, sensorEvents: false, hitEvents: false);
		ShapeOperations.SetEventFlags(dynamicShapeEntity, contactEvents: false, sensorEvents: false, hitEvents: true);
		Check("disabling contact events during overlap emits one deterministic end", endReceiver.ReadAll(static _ => { }) == 1);
		ShapeOperations.SetEventFlags(dynamicShapeEntity, contactEvents: true, sensorEvents: false, hitEvents: true);
		Check("re-enabling contact events during overlap emits one deterministic begin", receiver.ReadAll(static _ => { }) == 1);

		var unflaggedStatic = W.NewEntity<Default>();
		BodyOperations.CreateBody(unflaggedStatic, BodyType.Static,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(unflaggedStatic, Shape.MakeSphere(FVector3.Zero, FP.One));
		var unflaggedDynamic = W.NewEntity<Default>();
		BodyOperations.CreateBody(unflaggedDynamic, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var unflaggedShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		unflaggedShape.Density = FP.One;
		ShapeFactory.CreateShape(unflaggedDynamic, unflaggedShape);
		W.Tick();
		Systems.Update();
		Check("ordinary contacts without event flags emit no notification", receiver.ReadAll(static _ => { }) == 0);
		Shutdown();
	}

	private static void ContactHitEventTest() {
		Console.WriteLine("--- ContactHitEventTest ---");
		Bootstrap();
		var world = W.GetResource<PhysicsWorld>();
		world.Gravity = FVector3.Zero;
		world.HitEventThreshold = FP.Half;
		var receiver = W.RegisterEventReceiver<ContactHitEvent>();

		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(ground, Shape.MakeBox(FVector3.Zero, new FVector3(4.ToFP(), FP.Half, 4.ToFP())));
		var ball = W.NewEntity<Default>();
		BodyOperations.CreateBody(ball, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(7, 5), Fixed64.FP.Zero), FQuaternion.Identity));
		var ballShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		ballShape.Density = FP.One;
		ballShape.EnableHitEvents = true;
		ShapeFactory.CreateShape(ball, ballShape);
		BodyOperations.SetLinearVelocity(ball, new FVector3(FP.Zero, -5.ToFP(), FP.Zero));

		W.Tick();
		Systems.Update();
		var count = 0;
		var validPayload = false;
		foreach (var e in receiver) {
			count++;
			var pointY = Fixed64.FConversions.ToDouble(e.Value.Point.Y);
			validPayload |= e.Value.ApproachSpeed > world.HitEventThreshold
				&& e.Value.NormalImpulse > FP.Zero
				&& FVector3.LengthSqr(e.Value.Normal) > FP.Half
				&& pointY > 0.25 && pointY < 1.0;
		}
		Check("a solved high-speed impact emits one hit event", count == 1);
		Check("hit events include solved point, normal, approach speed, and positive impulse", validPayload);
		Shutdown();
	}

	private static void DropAndRestTest() {
		Console.WriteLine("--- DropAndRestTest (sphere on sphere) ---");
		Bootstrap();

		const double groundRadius = 5;
		const double ballRadius = 1;
		const double restY = groundRadius + ballRadius;
		const double startY = restY + 4; // 4 units above resting height

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio((int)groundRadius, 1)));

		var ballBody = W.NewEntity<Default>();
		ballBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio((int)startY, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(ballBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio((int)ballRadius, 1)));

		double firstY = startY;
		double minYSeen = startY;
		double lastY = startY;
		double lastSpeed = 0;

		const int totalTicks = 300;
		for (var tick = 0; tick < totalTicks; tick++) {
			W.Tick();
			Systems.Update();

			ref readonly var ball = ref ballBody.Read<Body>();
			var y = Fixed64.FConversions.ToDouble(ball.Transform.Position.Y);
			var speed = FVector3.Length(ball.LinearVelocity).ToDouble();
			minYSeen = Math.Min(minYSeen, y);
			lastY = y;
			lastSpeed = speed;

			if (tick == 0)
				firstY = y;
			if (tick % 30 == 0 || tick == totalTicks - 1) {
				Console.WriteLine($"  tick {tick,4}: y={y:F4} speed={speed:F4}");
			}
		}

		Check("ball falls from start height", firstY < startY - 0.001 || lastY < startY - 0.5);
		Check("ball never sinks meaningfully below resting height", minYSeen > restY - 0.1);
		Check($"ball settles near resting height {restY} (within 0.05)", Math.Abs(lastY - restY) < 0.05);
		Check("ball's speed is small at rest (< 0.5)", lastSpeed < 0.5);

		Shutdown();
	}

	/// <summary>
	/// A capsule dropped centered, axis-vertical, directly above the ground sphere — the analytic
	/// manifold's non-degenerate case for two shapes with real (anisotropic) rotational coupling.
	/// Deliberately NOT off-center: an off-center drop induces genuine tipping torque, and a capsule
	/// balanced on the curved shoulder of a sphere with only a single contact point is an inherently
	/// unstable configuration (like balancing a pencil on a basketball) — it can legitimately roll off,
	/// which isn't a bug. box3d's own near-parallel two-point capsule/capsule stability refinement
	/// (see Manifold.Collide's remarks) doesn't even apply here since this is capsule-vs-sphere.
	/// </summary>
	private static void CapsuleOnSphereSmokeTest() {
		Console.WriteLine("--- CapsuleOnSphereSmokeTest ---");
		Bootstrap();

		const double groundRadius = 5;
		const double capsuleRadius = 1;
		const double startY = groundRadius + capsuleRadius + 4;

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio((int)groundRadius, 1)));

		var capsuleBody = W.NewEntity<Default>();
		capsuleBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio((int)startY, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var capsuleShape = Shape.MakeCapsule(
			new FVector3(FP.Zero, -FP.Half, FP.Zero),
			new FVector3(FP.Zero, FP.Half, FP.Zero),
			FP.FromRatio((int)capsuleRadius, 1));
		// box3d's default density (1000, water) at radius-1 scale overflows Fixed32's range inside
		// FMatrix3.Determinant/InvertTranspose (the inertia's determinant lands around 1e10-1e11),
		// silently corrupting InvInertiaWorld with the wrong sign and magnitude. Spheres are immune
		// (a sphere's contact anchor is always parallel to the normal, so the rotational coupling
		// term is exactly zero regardless of what garbage is in InvInertiaWorld) — capsules aren't.
		// A sane density keeps mass/inertia well inside Fixed32's representable range. Worth knowing:
		// pick density/lengthUnitsPerMeter so mass * radius^2 stays modest for any non-sphere shape.
		capsuleShape.Density = FP.One;
		ShapeFactory.CreateShape(capsuleBody, capsuleShape);

		double lastY = startY;
		double lastSpeed = 0;
		var bounded = true;

		const int totalTicks = 300;
		for (var tick = 0; tick < totalTicks; tick++) {
			W.Tick();
			Systems.Update();

			ref readonly var capsule = ref capsuleBody.Read<Body>();
			var y = Fixed64.FConversions.ToDouble(capsule.Transform.Position.Y);
			var speed = FVector3.Length(capsule.LinearVelocity).ToDouble();

			if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1000) {
				bounded = false;
			}

			lastY = y;
			lastSpeed = speed;
			if (tick % 30 == 0 || tick == totalTicks - 1) {
				Console.WriteLine($"  tick {tick,4}: y={y:F4} speed={speed:F4}");
			}
		}

		Check("capsule stays numerically bounded (no NaN/explosion)", bounded);
		Check("capsule settles to a low speed", lastSpeed < 0.5);
		Check("capsule ends up resting well above the ground center (didn't fall through)", lastY > groundRadius);

		Shutdown();
	}

	/// <summary>
	/// A box dropped centered above the ground sphere. Exercises Manifold.Collide's hull/sphere path
	/// (the only manifold hull shapes currently participate in — see its remarks) and the Hull shape's
	/// mass/AABB computation end to end. Centered rather than off-center for the same reason as
	/// <see cref="CapsuleOnSphereSmokeTest"/>: resting on the shoulder of a sphere with one contact
	/// point is inherently unstable and can legitimately roll off.
	/// </summary>
	private static void BoxOnSphereSmokeTest() {
		Console.WriteLine("--- BoxOnSphereSmokeTest ---");
		Bootstrap();

		const double groundRadius = 5;
		const double boxHalfExtent = 1;
		const double startY = groundRadius + boxHalfExtent + 4;

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio((int)groundRadius, 1)));

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio((int)startY, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var boxShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
		// See CapsuleOnSphereSmokeTest's remarks: keep density sane so inertia stays inside Fixed32's range.
		boxShape.Density = FP.One;
		ShapeFactory.CreateShape(boxBody, boxShape);

		double lastY = startY;
		double lastSpeed = 0;
		var bounded = true;

		const int totalTicks = 300;
		for (var tick = 0; tick < totalTicks; tick++) {
			W.Tick();
			Systems.Update();

			ref readonly var box = ref boxBody.Read<Body>();
			var y = Fixed64.FConversions.ToDouble(box.Transform.Position.Y);
			var speed = FVector3.Length(box.LinearVelocity).ToDouble();

			if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1000) {
				bounded = false;
			}

			lastY = y;
			lastSpeed = speed;
			if (tick % 30 == 0 || tick == totalTicks - 1) {
				Console.WriteLine($"  tick {tick,4}: y={y:F4} speed={speed:F4}");
			}
		}

		Check("box stays numerically bounded (no NaN/explosion)", bounded);
		Check("box settles to a low speed", lastSpeed < 0.5);
		Check("box ends up resting well above the ground center (didn't fall through)", lastY > groundRadius);

		Shutdown();
	}

	/// <summary>
	/// A box dropped flat onto a static ground slab (box vs box). Exercises Manifold.Collide's
	/// hull/hull face-clip path end to end: resting flat on a face needs the multi-point manifold
	/// (box3d clips the reference face against the incident face) to stay put without tipping or
	/// jittering, unlike the single-point hull/sphere pair.
	/// </summary>
	private static void BoxOnBoxSmokeTest() {
		Console.WriteLine("--- BoxOnBoxSmokeTest ---");
		Bootstrap();

		const double groundHalfHeight = 0.5;
		const double boxHalfExtent = 1;
		const double startY = groundHalfHeight + boxHalfExtent + 4;

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(5, 1), FP.FromRatio(1, 2), FP.FromRatio(5, 1))));

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio((int)startY, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var boxShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
		boxShape.Density = FP.One;
		ShapeFactory.CreateShape(boxBody, boxShape);

		double lastY = startY;
		double lastSpeed = 0;
		var bounded = true;

		const int totalTicks = 300;
		for (var tick = 0; tick < totalTicks; tick++) {
			W.Tick();
			Systems.Update();

			ref readonly var box = ref boxBody.Read<Body>();
			var y = Fixed64.FConversions.ToDouble(box.Transform.Position.Y);
			var speed = FVector3.Length(box.LinearVelocity).ToDouble();

			if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1000) {
				bounded = false;
			}

			lastY = y;
			lastSpeed = speed;
			if (tick % 30 == 0 || tick == totalTicks - 1) {
				Console.WriteLine($"  tick {tick,4}: y={y:F4} speed={speed:F4}");
			}
		}

		Check("box stays numerically bounded (no NaN/explosion)", bounded);
		Check("box settles to a low speed", lastSpeed < 0.5);
		Check("box rests at the expected height on the ground slab", Math.Abs(lastY - (groundHalfHeight + boxHalfExtent)) < 0.1);

		Shutdown();
	}

	/// <summary>
	/// A box dropped centered above a horizontal ground capsule (box vs capsule). Exercises
	/// Manifold.Collide's hull/capsule face-clip path end to end.
	/// </summary>
	private static void BoxOnCapsuleSmokeTest() {
		Console.WriteLine("--- BoxOnCapsuleSmokeTest ---");
		Bootstrap();

		const double capsuleRadius = 1;
		const double boxHalfExtent = 1;
		const double startY = capsuleRadius + boxHalfExtent + 4;

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeCapsule(new FVector3(-FP.FromRatio(5, 1), FP.Zero, FP.Zero), new FVector3(FP.FromRatio(5, 1), FP.Zero, FP.Zero), FP.FromRatio((int)capsuleRadius, 1)));

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio((int)startY, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var boxShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
		boxShape.Density = FP.One;
		ShapeFactory.CreateShape(boxBody, boxShape);

		double lastY = startY;
		double lastSpeed = 0;
		var bounded = true;

		const int totalTicks = 300;
		for (var tick = 0; tick < totalTicks; tick++) {
			W.Tick();
			Systems.Update();

			ref readonly var box = ref boxBody.Read<Body>();
			var y = Fixed64.FConversions.ToDouble(box.Transform.Position.Y);
			var speed = FVector3.Length(box.LinearVelocity).ToDouble();

			if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1000) {
				bounded = false;
			}

			lastY = y;
			lastSpeed = speed;
			if (tick % 30 == 0 || tick == totalTicks - 1) {
				Console.WriteLine($"  tick {tick,4}: y={y:F4} speed={speed:F4}");
			}
		}

		Check("box stays numerically bounded (no NaN/explosion)", bounded);
		Check("box settles to a low speed", lastSpeed < 0.5);
		Check("box rests at the expected height on the capsule", Math.Abs(lastY - (capsuleRadius + boxHalfExtent)) < 0.1);

		Shutdown();
	}

	/// <summary>
	/// Reproduces a crash hit via the interactive visual test: rapidly spawning several bodies at the
	/// same/overlapping position (like mashing the drop key) creates deep initial penetration, which
	/// can drive the friction impulse's magnitude large enough that squaring it overflows Fixed32's
	/// ~32767 range — previously a crash (ArgumentOutOfRangeException from FP.Sqrt on a wrapped-negative
	/// value), now handled by ContactSolverSystem's box-clamped friction cone.
	/// </summary>
	private static void OverlappingSpawnStressTest() {
		Console.WriteLine("--- OverlappingSpawnStressTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(5, 1)));

		for (var i = 0; i < 15; i++) {
			var body = W.NewEntity<Default>();
			body.Set(new Body {
				Type = BodyType.Dynamic,
				GravityScale = FP.One,
				// Same position every time -- maximal overlap, worse than anything a human mashing a
				// key would realistically produce.
				Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(8, 1), Fixed64.FP.Zero), FQuaternion.Identity),
			});

			var shape = i % 2 == 0
				? Shape.MakeSphere(FVector3.Zero, FP.One)
				: Shape.MakeCapsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.One);
			shape.Density = FP.One;
			ShapeFactory.CreateShape(body, shape);
		}

		var threw = false;
		var bounded = true;
		try {
			for (var tick = 0; tick < 300; tick++) {
				W.Tick();
				Systems.Update();

				foreach (var entity in W.Query<All<Body>>().Entities()) {
					ref readonly var b = ref entity.Read<Body>();
					if (b.Type == BodyType.Static) {
						continue;
					}

					var y = Fixed64.FConversions.ToDouble(b.Transform.Position.Y);
					if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 100000) {
						bounded = false;
					}
				}
			}
		} catch (Exception e) {
			threw = true;
			Console.WriteLine($"  threw: {e}");
		}

		Check("15 overlapping bodies dropped at once do not crash the solver", !threw);
		Check("all bodies stay numerically bounded under heavy overlap", bounded);

		Shutdown();
	}

	/// <summary>
	/// Proves BroadPhase's DynamicTrees (not just plain ECS component data) survive a world-snapshot
	/// round-trip -- the scenario GameWorldRollback exercises every tick. Snapshots mid-fall (close
	/// approach, right as broadphase pairs/contacts are forming), advances the live world, restores
	/// the snapshot, then replays the same number of ticks and checks the replay matches what the
	/// live world actually did. A corrupted/empty tree would still run without throwing, but would
	/// desync contacts from the live reference run -- exactly the bug this test is designed to catch.
	/// </summary>
	private static void RollbackRoundTripTest() {
		Console.WriteLine("--- RollbackRoundTripTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(5, 1)));

		var ballBody = W.NewEntity<Default>();
		ballBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(ballBody, Shape.MakeSphere(FVector3.Zero, FP.One));

		// Step to a mid-fall point where broadphase pairs/contacts are actively forming.
		for (var tick = 0; tick < 55; tick++) {
			W.Tick();
			Systems.Update();
		}

		var (snapshotY, snapshotSpeed, snapshotContacts) = SampleState(ballBody);
		var snapshot = W.Serializer.CreateWorldSnapshot();

		// Advance the live world further -- this is what "the future we're about to roll back" looks like.
		const int replaySteps = 40;
		for (var tick = 0; tick < replaySteps; tick++) {
			W.Tick();
			Systems.Update();
		}

		var (liveY, liveSpeed, liveContacts) = SampleState(ballBody);

		// Restore the snapshot -- entity slots stay stable across a world-snapshot restore (that's
		// the premise GameWorldRollback itself relies on), so ballBody is still the right handle.
		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);

		var (restoredY, restoredSpeed, restoredContacts) = SampleState(ballBody);
		Check("restored state matches the snapshot point exactly", restoredY == snapshotY && restoredSpeed == snapshotSpeed && restoredContacts == snapshotContacts);

		// Replay the same number of ticks from the restored state and compare against the live reference run.
		for (var tick = 0; tick < replaySteps; tick++) {
			W.Tick();
			Systems.Update();
		}

		var (replayedY, replayedSpeed, replayedContacts) = SampleState(ballBody);
		Check("replay from a restored snapshot reproduces the live run's position", replayedY == liveY);
		Check("replay from a restored snapshot reproduces the live run's velocity", replayedSpeed == liveSpeed);
		Check("replay from a restored snapshot reproduces the live run's contact count", replayedContacts == liveContacts);

		Shutdown();
	}

	/// <summary>
	/// Phase 0 state-hash check: hashes the *complete* serialized world state (FNV-1a over the full
	/// world-snapshot bytes -- entities, components, events, BroadPhase trees/pairs) after every
	/// tick, then rolls back to a mid-run snapshot and replays. Where RollbackRoundTripTest samples
	/// three fields, this catches divergence in anything the serializer touches: rotation, contact
	/// impulses, proxies, pair caches, tree structure. BroadPhase.Write serializes pairs in sorted
	/// order precisely so this hash is independent of HashSet insertion history (see its remarks).
	/// </summary>
	private static void RollbackStateHashReplayTest() {
		Console.WriteLine("--- RollbackStateHashReplayTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var ballBody = W.NewEntity<Default>();
		ballBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var ballShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		ballShape.Density = FP.One;
		ShapeFactory.CreateShape(ballBody, ballShape);

		// A tumbling crate adds rotation, multi-point manifolds, and changing contact impulses to the
		// hashed state -- a falling sphere alone exercises too little of the snapshot.
		var crateBody = W.NewEntity<Default>();
		crateBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			LinearVelocity = new FVector3(FP.FromRatio(1, 2), FP.Zero, FP.Zero),
			AngularVelocity = new FVector3(FP.FromRatio(3, 10), FP.FromRatio(2, 10), FP.FromRatio(1, 10)),
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(14, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var crateShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
		crateShape.Density = FP.One;
		ShapeFactory.CreateShape(crateBody, crateShape);

		var hashWriter = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);

		ulong HashState() {
			hashWriter.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref hashWriter);
			return Fnv1a64(hashWriter.Buffer, (int)hashWriter.Position);
		}

		// Live reference run: warm up to the point where broad-phase pairs/contacts are forming, take
		// the rollback snapshot, then keep hashing every tick through landing and settling.
		const int warmupTicks = 55;
		const int replayTicks = 150;

		for (var tick = 0; tick < warmupTicks; tick++) {
			W.Tick();
			Systems.Update();
		}

		var snapshot = W.Serializer.CreateWorldSnapshot();
		var snapshotHash = HashState();

		var liveHashes = new ulong[replayTicks];
		for (var tick = 0; tick < replayTicks; tick++) {
			W.Tick();
			Systems.Update();
			liveHashes[tick] = HashState();
		}

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		Check("restored world reproduces the snapshot tick's state hash exactly", HashState() == snapshotHash);

		var replayHashes = new ulong[replayTicks];
		for (var tick = 0; tick < replayTicks; tick++) {
			W.Tick();
			Systems.Update();
			replayHashes[tick] = HashState();
		}

		// Consumed by CI, which runs this harness in Debug and Release and requires identical hashes.
		Console.WriteLine($"STATE-HASH rollback-replay 0x{liveHashes[replayTicks - 1].ToString("x16")}");

		var divergedAt = -1;
		for (var tick = 0; tick < replayTicks; tick++) {
			if (replayHashes[tick] != liveHashes[tick]) {
				divergedAt = tick;
				break;
			}
		}

		var divergenceDetail = divergedAt >= 0 ? $" -- first divergence at tick {divergedAt}: live 0x{liveHashes[divergedAt].ToString("x16")} vs replay 0x{replayHashes[divergedAt].ToString("x16")}" : "";
		Check($"replay from a restored snapshot reproduces all {replayTicks} per-tick state hashes{divergenceDetail}", divergedAt == -1);

		Shutdown();
	}

	/// <summary>
	/// Regression test for the Transform/Body desync bug: gameplay code used to write the
	/// gameplay-facing Transform.Position directly while collision
	/// (ShapeProxySystem/ContactSystem/ContactSolverSystem) only ever looks at Body.Transform, so a
	/// kinematic body driven that way never actually moved in physics space and could never touch
	/// anything. This drives a kinematic body the correct way -- via Body.LinearVelocity, letting the
	/// solver move Body.Transform -- and checks both that BodyTransformSyncSystem mirrors the result
	/// into Transform, and that the moving kinematic body actually collides with and pushes a dynamic
	/// body in its path.
	/// </summary>
	private static void KinematicBodyDrivenByVelocityPushesDynamicBodyTest() {
		Console.WriteLine("--- KinematicBodyDrivenByVelocityPushesDynamicBodyTest ---");
		Bootstrap();

		var moverBody = W.NewEntity<Default>();
		moverBody.Set(new Transform { Rotation = Fixed64.FQuaternion.Identity });
		moverBody.Set(new Body {
			Type = BodyType.Kinematic,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(moverBody, Shape.MakeSphere(FVector3.Zero, FP.One));

		var targetBody = W.NewEntity<Default>();
		targetBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.Zero,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(6, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var targetShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		targetShape.Density = FP.One;
		ShapeFactory.CreateShape(targetBody, targetShape);

		var startTargetX = targetBody.Read<Body>().Transform.Position.X;

		ref var mover = ref moverBody.Mut<Body>();
		mover.LinearVelocity = new FVector3(FP.FromRatio(3, 1), FP.Zero, FP.Zero);

		for (var tick = 0; tick < 120; tick++) {
			W.Tick();
			Systems.Update();
		}

		var moverBodyX = moverBody.Read<Body>().Transform.Position.X;
		var moverTransformX = moverBody.Read<Transform>().Position.X;
		var endTargetX = targetBody.Read<Body>().Transform.Position.X;

		Check("kinematic body actually moves via Body.LinearVelocity", moverBodyX > FP.FromRatio(2, 1).To64());
		Check("BodyTransformSyncSystem mirrors Body.Transform into Transform", moverTransformX == moverBodyX);
		Check("moving kinematic body pushes the dynamic body it collides with", endTargetX > startTargetX);

		Shutdown();
	}

	/// <summary>
	/// Runs one CharacterMover step exactly as PlayerMoverSystem does per tick (see its remarks):
	/// collide -&gt; solve -&gt; cast -&gt; move, repeated up to 5 times, then the dynamic-body push impulse
	/// and velocity clip. Standalone here since these are plain functions over BroadPhase/Capsule --
	/// no ECS Transform/Mover components or Session needed, so the algorithm is testable directly.
	/// </summary>
	private static void StepMover(BroadPhase broadPhase, ref FWorldTransform moverXf, ref FVector3 moverVelocity, Capsule capsule, FVector3 desiredVelocity, FP dt) {
		moverVelocity = desiredVelocity;
		var target = moverXf.Position + dt * moverVelocity;

		var planes = new MoverPlaneBuffer();
		var tolerance = FP.FromRatio(1, 100);
		for (var iteration = 0; iteration < 5; iteration++) {
			planes.Clear();
			CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);

			var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
			var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
			delta *= fraction;
			moverXf.Position += delta;

			if (FVector3.LengthSqr(delta) < tolerance * tolerance) {
				break;
			}
		}

		CharacterMover.ApplyPushImpulses(moverXf, moverVelocity, in planes);
		moverVelocity = MoverSolver.ClipVector(moverVelocity, in planes);
	}

	/// <summary>
	/// Same as <see cref="StepMover"/> but mirrors PlayerMoverSystem's full per-tick logic including
	/// gravity, box3d's pogo-stick ground check, and the jump-cooldown gate (see
	/// Mover.JumpCooldown's remarks) that keeps a fresh jump from being immediately re-grounded and
	/// zeroed out one tick later.
	/// </summary>
	private static void StepMoverWithGravity(BroadPhase broadPhase, ref FWorldTransform moverXf, ref FVector3 moverVelocity, ref bool grounded, ref FP pogoVelocity, ref FP jumpCooldown, Capsule capsule, FVector3 horizontalVelocity, bool jumpPressed, FVector3 gravity, FP jumpForce, FP dt) {
		// Matches CharacterRes's defaults (0.2s).
		var jumpCooldownTime = FP.FromRatio(2, 10);

		if (jumpPressed && grounded) {
			moverVelocity.Y = jumpForce;
			grounded = false;
			jumpCooldown = jumpCooldownTime;
		} else if (grounded) {
			moverVelocity.Y = FP.Zero;
		}

		jumpCooldown = FP.Max(FP.Zero, jumpCooldown - dt);

		moverVelocity = new FVector3(horizontalVelocity.X, moverVelocity.Y + gravity.Y * dt, horizontalVelocity.Z);

		// Matches CharacterRes's defaults (4 Hz, 0.7 damping ratio).
		grounded = CharacterMover.UpdatePogoGrounding(broadPhase, moverXf, capsule, dt, FP.FromRatio(4, 1), FP.FromRatio(7, 10), jumpCooldown, FP.FromRatio(7071, 10000), ref pogoVelocity);

		var target = moverXf.Position + dt * moverVelocity + dt * pogoVelocity * FVector3.Up;

		var planes = new MoverPlaneBuffer();
		var tolerance = FP.FromRatio(1, 100);
		for (var iteration = 0; iteration < 5; iteration++) {
			planes.Clear();
			CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);

			var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
			var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
			delta *= fraction;
			moverXf.Position += delta;

			if (FVector3.LengthSqr(delta) < tolerance * tolerance) {
				break;
			}
		}

		CharacterMover.ApplyPushImpulses(moverXf, moverVelocity, in planes);
		moverVelocity = MoverSolver.ClipVector(moverVelocity, in planes);
	}

	/// <summary>
	/// Regression test for gravity/ground-detection/jump (PlayerMoverSystem): a mover dropped above
	/// the ground falls and settles feet-flush with the pogo suspension's equilibrium height (see
	/// CharacterMover.UpdatePogoGrounding's remarks on why this project uses
	/// <c>pogoRestLength = radius</c>, not box3d's literal <c>3*radius</c>), and is reported
	/// grounded; jumping applies an upward velocity impulse and visibly displaces the mover above
	/// that equilibrium; it then falls back and lands again. Unlike the old plane-derived check,
	/// grounded is recomputed from the *pre-movement* position every tick (matching box3d's own
	/// ordering: the ray runs before SolveMove's movement loop), so it isn't expected to clear in
	/// the exact same tick a jump is pressed -- only once the mover has actually risen out of the
	/// ray's range on a later tick (or immediately, here, via Mover.JumpCooldown).
	/// </summary>
	private static void MoverFallsLandsAndJumpsTest() {
		Console.WriteLine("--- MoverFallsLandsAndJumpsTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var broadPhase = W.GetResource<BroadPhase>();
		// Capsule centered on the mover's own Transform origin, matching Player.cs's real convention
		// (Transform.Position is the capsule's center, like every other Body-owning entity in this
		// project -- see Player.cs's remarks).
		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var grounded = false;
		var pogoVelocity = FP.Zero;
		var jumpCooldown = FP.Zero;
		var gravity = new FVector3(FP.Zero, -FP.FromRatio(10, 1), FP.Zero);
		var jumpForce = FP.FromRatio(12, 1);
		var dt = Space.GameCore.Const.DeltaTime.To32();

		// Ground top is at y=0.5; the pogo ray hovers Center1 (y=-0.5 local) at pogoRestLength=
		// radius=0.5 above that -- equilibrium Center1_world = 0.5 + 0.5 = 1.0, so
		// moverXf.Position.Y = 1.0 - Center1.Y(-0.5) = 1.5, i.e. feet (Center1 - radius) flush with
		// the ground. Matches Player.cs's spawn Y exactly.
		const double equilibriumY = 1.5;

		for (var tick = 0; tick < 90; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();

		}

		var yAfterFalling = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		Console.WriteLine($"  after falling: y={yAfterFalling:F4} grounded={grounded}");
		Check($"mover lands and settles at the pogo equilibrium height (~{equilibriumY})", Math.Abs(yAfterFalling - equilibriumY) < 0.05);
		Check("mover is reported grounded after landing", grounded);

		// Jump for exactly one tick (edge-triggered, like Godot's IsActionJustPressed).
		StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, FVector3.Zero, true, gravity, jumpForce, dt);
		Check("jump applies a large upward velocity impulse", moverVelocity.Y.ToDouble() > jumpForce.ToDouble() * 0.9);
		W.Tick();
		Systems.Update();

		// Jump force 12 over gravity 10 takes ~1.2s (72 ticks) just to reach the peak -- give the
		// ascent loop enough budget to actually capture it, not cut it off mid-flight.
		var maxYAfterJump = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		for (var tick = 0; tick < 80; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();
			maxYAfterJump = Math.Max(maxYAfterJump, Fixed64.FConversions.ToDouble(moverXf.Position.Y));
		}

		Console.WriteLine($"  peak height after jump: {maxYAfterJump:F4} (equilibrium: {equilibriumY})");
		Check("jump visibly displaces the mover above the pogo equilibrium height", maxYAfterJump > equilibriumY + 0.1);

		// Let it fall back down and land again -- descent from the peak takes about as long as the
		// ascent did, plus margin to actually settle.
		for (var tick = 0; tick < 200; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();
		}

		var yAfterLandingAgain = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		Console.WriteLine($"  after falling back down: y={yAfterLandingAgain:F4} grounded={grounded}");
		Check("mover lands again after the jump", Math.Abs(yAfterLandingAgain - equilibriumY) < 0.05);
		Check("mover is grounded again after landing", grounded);

		Shutdown();
	}

	/// <summary>
	/// Regression test for the kinematic-body pushing bug: driving the player as a plain Kinematic
	/// Body straight through the contact solver (the old MovementSystem) caused a violent velocity
	/// spike on first contact (sphere speed jumped to ~7, matching the mover's own speed almost 1:1)
	/// and briefly punched the sphere through the ground (y dropping to ~0.23 against a 0.5 ground
	/// top). Same spawn parameters as SpawnSphereSystem/SpawnPlayerSystem; drives the mover via
	/// <see cref="StepMover"/> instead of a Body this time.
	/// </summary>
	private static void MoverPushesRestingSphereStablyTest() {
		Console.WriteLine("--- MoverPushesRestingSphereStablyTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var spherePosition = FVector3.Up * 50 + FVector3.Forward * 2;
		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(FPos.FromLocal(spherePosition), FQuaternion.Identity),
		});
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		sphereShape.Density = FP.One;
		ShapeFactory.CreateShape(sphereBody, sphereShape);

		// Let the sphere settle on the ground first, same as the real game does before a player ever reaches it.
		for (var tick = 0; tick < 200; tick++) {
			W.Tick();
			Systems.Update();
		}

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var moveVelocity = new FVector3(FP.Zero, FP.Zero, FP.FromRatio(7, 1));
		var dt = Space.GameCore.Const.DeltaTime.To32();

		var minSphereY = double.MaxValue;
		var maxSphereSpeed = 0.0;

		for (var tick = 0; tick < 180; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, moveVelocity, dt);

			W.Tick();
			Systems.Update();

			var (_, sy, _, speed) = SampleSphere(sphereBody);
			minSphereY = Math.Min(minSphereY, sy);
			maxSphereSpeed = Math.Max(maxSphereSpeed, speed);

			if (tick % 30 == 0) {
				var (sx, _, sz, _) = SampleSphere(sphereBody);
				Console.WriteLine($"  tick {tick,3}: sphere pos=({sx:F3},{sy:F3},{sz:F3}) speed={speed:F3}");
			}
		}

		var (_, _, endZ, _) = SampleSphere(sphereBody);

		// A sphere carried along by an infinite-mass pusher in continuous contact correctly settles
		// to *matching* the pusher's speed (7) -- that's not instability. What the old kinematic-Body
		// bug actually did was momentarily overshoot past that (peaking at ~7.096) before crashing
		// through the ground; this checks for genuine runaway energy gain, not "reached 7".
		Check("sphere speed never runs away past the mover's own push speed", maxSphereSpeed < 7.5);
		Check("sphere never sinks through the ground while being pushed (was dropping to ~0.23)", minSphereY > 1.0);
		Check("mover still displaces the sphere over time", endZ > 2.5);

		Shutdown();
	}

	/// <summary>
	/// A sphere is round: Coulomb friction only kills *sliding* at the contact point, so a pushed
	/// sphere converts sliding into rolling almost immediately and then, with no rolling resistance,
	/// keeps rolling forever (verified interactively -- linear/angular speed stayed ~7 for 4+ seconds
	/// with zero decay). This checks the fix: a shape with SurfaceMaterial.RollingResistance &gt; 0
	/// actually decelerates and comes to rest after the push stops, matching SpawnSphereSystem's sphere.
	/// </summary>
	private static void RollingResistanceBringsPushedSphereToRestTest() {
		Console.WriteLine("--- RollingResistanceBringsPushedSphereToRestTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var spherePosition = FVector3.Up * 50 + FVector3.Forward * 2;
		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(FPos.FromLocal(spherePosition), FQuaternion.Identity),
		});
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		sphereShape.Density = FP.One;
		sphereShape.Material.RollingResistance = FP.FromRatio(1, 4); // matches SpawnSphereSystem's sphere
		ShapeFactory.CreateShape(sphereBody, sphereShape);

		for (var tick = 0; tick < 200; tick++) {
			W.Tick();
			Systems.Update();
		}

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var pushVelocity = new FVector3(FP.Zero, FP.Zero, FP.FromRatio(7, 1));
		var dt = Space.GameCore.Const.DeltaTime.To32();

		// Push for 1 second, then release (mover stays put with zero velocity).
		for (var tick = 0; tick < 60; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, pushVelocity, dt);
			W.Tick();
			Systems.Update();
		}

		var speedRightAfterRelease = FVector3.Length(sphereBody.Read<Body>().LinearVelocity).ToDouble();

		double lastSpeed = speedRightAfterRelease;
		for (var tick = 0; tick < 600; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, FVector3.Zero, dt);
			W.Tick();
			Systems.Update();

			lastSpeed = FVector3.Length(sphereBody.Read<Body>().LinearVelocity).ToDouble();
			if (tick % 60 == 0 || tick == 599) {
				Console.WriteLine($"  tick {tick,3}: linearSpeed={lastSpeed:F4}");
			}
		}

		Check("sphere is actually rolling right after the push (speed near the push speed of 7)", speedRightAfterRelease > 5.0);
		Check("rolling resistance brings the released sphere to rest (was: never decayed at all)", lastSpeed < 0.5);

		Shutdown();
	}

	/// <summary>
	/// New capability check: a plain kinematic Body was never blocked by anything it hit (zero
	/// mass, unaffected by contacts). The mover must actually stop at solid geometry.
	/// </summary>
	private static void MoverBlockedByStaticWallTest() {
		Console.WriteLine("--- MoverBlockedByStaticWallTest ---");
		Bootstrap();

		var wallBody = W.NewEntity<Default>();
		wallBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(5, 1)), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(wallBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(5, 1), FP.FromRatio(5, 1), FP.Half)));

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var moveVelocity = new FVector3(FP.Zero, FP.Zero, FP.FromRatio(7, 1));
		var dt = Space.GameCore.Const.DeltaTime.To32();

		for (var tick = 0; tick < 180; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, moveVelocity, dt);
			W.Tick();
			Systems.Update();
		}

		var moverZ = Fixed64.FConversions.ToDouble(moverXf.Position.Z);
		Console.WriteLine($"  final moverZ={moverZ:F3} (wall near face at z=4.5; unblocked would reach ~21)");

		// Wall's near face is at z = 5 - 0.5 = 4.5; the capsule radius keeps its center further back still.
		Check("mover is blocked by a solid wall instead of tunneling through it", moverZ < 4.5);

		Shutdown();
	}

	/// <summary>
	/// Regression test for the production crash this session found and fixed: GJK's barycentric
	/// coordinate math (GJK.BarycentricCoordsTri/Tet, box3d's own raw-point formula from
	/// distance.c) silently overflowed Fixed32 against this project's real ground size (40 units
	/// across), corrupting simplex blend weights badly enough that a downstream Sqrt call received a
	/// wildly out-of-range value and threw an unhandled exception -- a full server crash, not just a
	/// wrong answer. Walks and jumps the mover in a widening spiral that repeatedly crosses on and
	/// off the real 40x0.5x40 ground (SpawnSphereSystem's actual size, not a scaled-down test
	/// fixture), covering a broad spread of simplex configurations against CastMover/CollideMover
	/// and CharacterMover.UpdatePogoGrounding's own box-cast, for long enough (many thousands of
	/// ticks) to have a real chance of hitting whatever specific configuration crashed in practice.
	/// Uses Player.cs's actual capsule dimensions (center-origin, not the older feet-origin numbers
	/// some of the other tests still use) since that's what's actually deployed.
	/// </summary>
	private static void MoverStressTestAgainstRealGroundSize() {
		Console.WriteLine("--- MoverStressTestAgainstRealGroundSize ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3, 2), Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var grounded = false;
		var pogoVelocity = FP.Zero;
		var jumpCooldown = FP.Zero;
		var gravity = new FVector3(FP.Zero, -FP.FromRatio(10, 1), FP.Zero);
		var jumpForce = FP.FromRatio(8, 1);
		var dt = Space.GameCore.Const.DeltaTime.To32();

		var threw = false;
		var bounded = true;
		Exception? caught = null;

		try {
			const int totalTicks = 6000;
			for (var tick = 0; tick < totalTicks; tick++) {
				// Direction sweeps a full turn every 120 ticks (2s) while moving forward continuously,
				// tracing a loose spiral/loop across the ground -- repeatedly crossing every edge and
				// revisiting the interior, not just one straight line off the side. Jump period (47)
				// deliberately doesn't divide the turn period, so jumps land at varied points in the turn.
				var angle = FP.FromRatio(tick % 120, 1) * (2 * FP.Pi) / FP.FromRatio(120, 1);
				var horizontalVelocity = new FVector3(FP.Sin(angle) * FP.FromRatio(7, 1), FP.Zero, FP.Cos(angle) * FP.FromRatio(7, 1));
				var jumpPressed = tick % 47 == 0;

				StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, horizontalVelocity, jumpPressed, gravity, jumpForce, dt);
				W.Tick();
				Systems.Update();

				var y = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
				if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 100000) {
					bounded = false;
				}
			}
		} catch (Exception e) {
			threw = true;
			caught = e;
		}

		if (threw) {
			Console.WriteLine($"  threw: {caught}");
		}

		Check("wide movement across the real-sized ground does not crash GJK (was: unhandled ArgumentOutOfRangeException from Sqrt)", !threw);
		Check("mover position stays numerically bounded throughout", bounded);

		Shutdown();
	}

	/// <summary>
	/// Regression test for the sensor solid-response bug (Shape.cs's own doc comment: a sensor
	/// "generates overlap events but never generates a collision response" -- ContactSolverSystem
	/// used to ignore IsSensor entirely). A sensor box sits directly in a falling sphere's path
	/// above the real ground; the sphere must pass straight through it (no deflection, no resting on
	/// top of it) while still firing touch events for the overlap.
	/// </summary>
	private static void SensorDoesNotProduceCollisionResponseTest() {
		Console.WriteLine("--- SensorDoesNotProduceCollisionResponseTest ---");
		Bootstrap();

		var beginReceiver = W.RegisterEventReceiver<SensorBeginTouchEvent>();
		var endReceiver = W.RegisterEventReceiver<SensorEndTouchEvent>();
		var contactBeginReceiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var sensorBody = W.NewEntity<Default>();
		sensorBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var sensorShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(2, 1), FP.Half, FP.FromRatio(2, 1)));
		sensorShape.IsSensor = true;
		sensorShape.EnableSensorEvents = true;
		var sensorShapeEntity = ShapeFactory.CreateShape(sensorBody, sensorShape);

		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		sphereShape.Density = FP.One;
		sphereShape.EnableSensorEvents = true;
		var sphereShapeEntity = ShapeFactory.CreateShape(sphereBody, sphereShape);

		var sawSensorTouch = false;
		var sawSensorEnd = false;
		var sensorOrderingCorrect = false;
		var minY = 10.0;

		for (var tick = 0; tick < 300; tick++) {
			W.Tick();
			Systems.Update();

			foreach (var e in beginReceiver) {
				sawSensorTouch = true;
				sensorOrderingCorrect |= e.Value.SensorShape == sensorShapeEntity.GID && e.Value.VisitorShape == sphereShapeEntity.GID;
			}
			if (endReceiver.ReadAll(static _ => { }) > 0) {
				sawSensorEnd = true;
			}

			var y = Fixed64.FConversions.ToDouble(sphereBody.Read<Body>().Transform.Position.Y);
			minY = Math.Min(minY, y);
		}

		var finalY = Fixed64.FConversions.ToDouble(sphereBody.Read<Body>().Transform.Position.Y);

		Check("dedicated sensor events fire for the sensor overlap", sawSensorTouch);
		Check("sensor events identify the sensor and visitor deterministically", sensorOrderingCorrect);
		Check("dedicated sensor end events fire after separation", sawSensorEnd);
		Check("sensor overlaps do not emit ordinary contact events", contactBeginReceiver.ReadAll(static _ => { }) == 0);
		Check("sphere falls straight through the sensor instead of resting on it (rests at ~1.5, not ~6.5)", Math.Abs(finalY - 1.5) < 0.05);
		Check("sphere never got hung up on the sensor on the way down (dipped below its bottom face at 4.5)", minY < 4.5);

		Shutdown();
	}

	private static void SensorSensorDirectionalEventsTest() {
		Console.WriteLine("--- SensorSensorDirectionalEventsTest ---");
		Bootstrap();
		var receiver = W.RegisterEventReceiver<SensorBeginTouchEvent>();
		var bodyA = W.NewEntity<Default>();
		BodyOperations.CreateBody(bodyA, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var sensorA = Shape.MakeSphere(FVector3.Zero, FP.One);
		sensorA.IsSensor = true;
		sensorA.EnableSensorEvents = true;
		var shapeA = ShapeFactory.CreateShape(bodyA, sensorA);
		var bodyB = W.NewEntity<Default>();
		BodyOperations.CreateBody(bodyB, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var sensorB = Shape.MakeSphere(FVector3.Zero, FP.One);
		sensorB.IsSensor = true;
		sensorB.EnableSensorEvents = true;
		var shapeB = ShapeFactory.CreateShape(bodyB, sensorB);

		W.Tick();
		Systems.Update();
		var count = 0;
		var sawA = false;
		var sawB = false;
		foreach (var e in receiver) {
			count++;
			sawA |= e.Value.SensorShape == shapeA.GID && e.Value.VisitorShape == shapeB.GID;
			sawB |= e.Value.SensorShape == shapeB.GID && e.Value.VisitorShape == shapeA.GID;
		}
		Check("sensor/sensor overlap emits one directional event for each sensor", count == 2 && sawA && sawB);
		Shutdown();
	}

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

	private static void RotatingTimeOfImpactTest() {
		Console.WriteLine("--- RotatingTimeOfImpactTest ---");
		var endRotation = FQuaternion.AxisAngleRadians(FVector3.Forward, FP.Pi);
		var midpoint = FQuaternion.Nlerp(FQuaternion.Identity, endRotation, FP.Half) * new FVector3(3.ToFP(), FP.Zero, FP.Zero);
		var obstacle = new ShapeProxy(new[] { FVector3.Zero }, FP.FromRatio(1, 5));
		var rotatingCapsule = new ShapeProxy(new[] { FVector3.Zero, new FVector3(3.ToFP(), FP.Zero, FP.Zero) }, FP.FromRatio(1, 10));
		var output = Distance.TimeOfImpact(new TimeOfImpactInput {
			ProxyA = obstacle,
			ProxyB = rotatingCapsule,
			TransformAStart = new FWorldTransform(FPos.FromLocal(midpoint), FQuaternion.Identity),
			TransformAEnd = new FWorldTransform(FPos.FromLocal(midpoint), FQuaternion.Identity),
			TransformBStart = FWorldTransform.Identity,
			TransformBEnd = new FWorldTransform(FPos.Zero, endRotation),
			MaxFraction = FP.One,
		});
		Check("rotating convex TOI detects an intermediate rotational-only collision", output.State == TimeOfImpactState.Hit && output.Fraction > FP.Zero && output.Fraction < FP.One);

		var localCenter = new FVector3(2.ToFP(), FP.Zero, FP.Zero);
		var endOrigin = localCenter - endRotation * localCenter;
		var incorrectMidpoint = FP.Half * endOrigin + FQuaternion.Nlerp(FQuaternion.Identity, endRotation, FP.Half) * localCenter;
		var centeredOutput = Distance.TimeOfImpact(new TimeOfImpactInput {
			ProxyA = obstacle,
			ProxyB = new ShapeProxy(new[] { localCenter }, FP.FromRatio(1, 10)),
			TransformAStart = new FWorldTransform(FPos.FromLocal(incorrectMidpoint), FQuaternion.Identity),
			TransformAEnd = new FWorldTransform(FPos.FromLocal(incorrectMidpoint), FQuaternion.Identity),
			TransformBStart = FWorldTransform.Identity,
			TransformBEnd = new FWorldTransform(FPos.FromLocal(endOrigin), endRotation),
			LocalCenterB = localCenter,
			MaxFraction = FP.One,
		});
		Check("rotating TOI reconstructs intermediate origins around the center of mass", centeredOutput.State == TimeOfImpactState.Separated);
	}

	private static void AutomaticFastBodyCcdTest() {
		Console.WriteLine("--- AutomaticFastBodyCcdTest ---");
		Bootstrap();
		var wall = W.NewEntity<Default>();
		BodyOperations.CreateBody(wall, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(3, 4), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(wall, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(1, 20), 2.ToFP(), 2.ToFP())));
		var fastBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(fastBody, BodyType.Dynamic, FWorldTransform.Identity);
		var fastShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 10));
		fastShape.Density = FP.One;
		ShapeFactory.CreateShape(fastBody, fastShape);
		BodyOperations.SetLinearVelocity(fastBody, new FVector3(60.ToFP(), FP.Zero, FP.Zero));
		var fastBodyGid = fastBody.GID;
		var snapshot = W.Serializer.CreateWorldSnapshot();
		W.Tick();
		Systems.Update();
		Check("unmarked fast dynamic body receives automatic CCD against static geometry", fastBody.Read<Body>().Transform.Position.X < Fixed64.FP.FromRatio(3, 4));
		var liveState = W.Serializer.CreateWorldSnapshot();
		var liveX = fastBody.Read<Body>().Transform.Position.X;
		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		W.Tick();
		Systems.Update();
		var replayState = W.Serializer.CreateWorldSnapshot();
		Check("automatic fast-body CCD reproduces exactly after rollback", fastBodyGid.TryUnpack<TestWorld>(out var replayBody)
			&& replayBody.Read<Body>().Transform.Position.X == liveX
			&& Fnv1a64(replayState, replayState.Length) == Fnv1a64(liveState, liveState.Length));
		Shutdown();
	}

	private static void AngularCcdTest() {
		Console.WriteLine("--- AngularCcdTest ---");
		Bootstrap();
		var obstacle = W.NewEntity<Default>();
		BodyOperations.CreateBody(obstacle, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(7, 4), Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(obstacle, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 5)));
		var rotor = W.NewEntity<Default>();
		BodyOperations.CreateBody(rotor, BodyType.Dynamic, FWorldTransform.Identity);
		var rotorShape = Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.FromRatio(1, 10), FP.FromRatio(1, 10)));
		rotorShape.Density = FP.One;
		ShapeFactory.CreateShape(rotor, rotorShape);
		BodyOperations.SetAngularVelocity(rotor, new FVector3(FP.Zero, FP.Zero, 30.ToFP()));
		var rotorGid = rotor.GID;
		var snapshot = W.Serializer.CreateWorldSnapshot();
		W.Tick();
		Systems.Update();
		var angle = FQuaternion.GetAngle(rotor.Read<Body>().Transform.Rotation);
		Check("automatic CCD clips a rotating thin body at its intermediate impact", angle > FP.Zero && angle < FP.FromRatio(9, 20));
		var liveState = W.Serializer.CreateWorldSnapshot();
		var liveRotation = rotor.Read<Body>().Transform.Rotation;
		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		W.Tick();
		Systems.Update();
		var replayState = W.Serializer.CreateWorldSnapshot();
		Check("angular CCD reproduces the full clipped pose after rollback", rotorGid.TryUnpack<TestWorld>(out var replayRotor)
			&& replayRotor.Read<Body>().Transform.Rotation == liveRotation
			&& Fnv1a64(replayState, replayState.Length) == Fnv1a64(liveState, liveState.Length));
		Shutdown();
	}

	private static void BulletCcdTargetPolicyTest() {
		Console.WriteLine("--- BulletCcdTargetPolicyTest ---");
		static bool Hits(BodyType sourceType, BodyType targetType, bool targetIsBullet) {
			Bootstrap();
			var target = W.NewEntity<Default>();
			BodyOperations.CreateBody(target, targetType, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(3, 4), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
			if (targetIsBullet)
				BodyOperations.SetBullet(target, true);
			var targetShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 5));
			targetShape.Density = FP.One;
			ShapeFactory.CreateShape(target, targetShape);
			var bullet = W.NewEntity<Default>();
			BodyOperations.CreateBody(bullet, sourceType, FWorldTransform.Identity);
			BodyOperations.SetBullet(bullet, true);
			BodyOperations.SetLinearVelocity(bullet, new FVector3(60.ToFP(), FP.Zero, FP.Zero));
			var bulletShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 10));
			bulletShape.Density = FP.One;
			ShapeFactory.CreateShape(bullet, bulletShape);
			W.Tick();
			Systems.Update();
			var bulletX = bullet.Read<Body>().Transform.Position.X;
			var targetX = target.Read<Body>().Transform.Position.X;
			var hit = bulletX < targetX;
			Shutdown();
			return hit;
		}

		Check("explicit kinematic bullets sweep against static targets", Hits(BodyType.Kinematic, BodyType.Static, false));
		Check("explicit kinematic bullets sweep against kinematic targets", Hits(BodyType.Kinematic, BodyType.Kinematic, false));
		Check("explicit kinematic bullets sweep against dynamic targets", Hits(BodyType.Kinematic, BodyType.Dynamic, false));
		Check("explicit dynamic bullets sweep against kinematic targets", Hits(BodyType.Dynamic, BodyType.Kinematic, false));
		Check("explicit bullets do not sweep against other explicit bullets", !Hits(BodyType.Kinematic, BodyType.Kinematic, true));

		Bootstrap();
		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, FWorldTransform.Identity);
		var rejected = false;
		try {
			BodyOperations.SetBullet(staticBody, true);
		} catch (InvalidOperationException) {
			rejected = true;
		}
		var mutableBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(mutableBody, BodyType.Kinematic, FWorldTransform.Identity);
		BodyOperations.SetBullet(mutableBody, true);
		BodyOperations.SetType(mutableBody, BodyType.Static);
		Check("static bodies reject or clear bullet state", rejected && !mutableBody.Read<Body>().IsBullet);
		Shutdown();
	}

	private static void MaximumSpeedBulletCcdTest() {
		Console.WriteLine("--- MaximumSpeedBulletCcdTest ---");
		Bootstrap();
		var wall = W.NewEntity<Default>();
		BodyOperations.CreateBody(wall, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(3, 4), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(wall, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(1, 20), 2.ToFP(), 2.ToFP())));
		var bullet = W.NewEntity<Default>();
		BodyOperations.CreateBody(bullet, BodyType.Kinematic, FWorldTransform.Identity);
		BodyOperations.SetBullet(bullet, true);
		BodyOperations.SetLinearVelocity(bullet, new FVector3(W.GetResource<PhysicsWorld>().MaximumLinearSpeed, FP.Zero, FP.Zero));
		ShapeFactory.CreateShape(bullet, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 10)));
		W.Tick();
		Systems.Update();
		Check("configured maximum-speed bullet cannot tunnel through the minimum thin-wall fixture",
			bullet.Read<Body>().Transform.Position.X < Fixed64.FP.FromRatio(3, 4));
		Shutdown();
	}

	private static void CharacterMoverFilterTest() {
		Console.WriteLine("--- CharacterMoverFilterTest ---");
		Bootstrap();
		var wall = W.NewEntity<Default>();
		BodyOperations.CreateBody(wall, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(2, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		var wallShape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.Quarter, 2.ToFP(), 2.ToFP()));
		wallShape.Filter.CategoryBits = 2;
		var wallShapeEntity = ShapeFactory.CreateShape(wall, wallShape);
		W.Tick();
		Systems.Update();

		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var translation = new FVector3(4.ToFP(), FP.Zero, FP.Zero);
		var broadPhase = W.GetResource<BroadPhase>();
		var blocked = CharacterMover.CastMover(broadPhase, FWorldTransform.Identity, capsule, translation, FP.One, Filter.Default);
		var excludedFilter = new Filter { CategoryBits = ulong.MaxValue, MaskBits = 1, GroupIndex = 0 };
		var excluded = CharacterMover.CastMover(broadPhase, FWorldTransform.Identity, capsule, translation, FP.One, excludedFilter);
		Check("character mover is blocked by an allowed wall", blocked < FP.One);
		Check("character mover respects category and mask filtering", excluded == FP.One);

		var overlapTransform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(2, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity);
		var allowedPlanes = new MoverPlaneBuffer();
		CharacterMover.CollideMover(broadPhase, overlapTransform, capsule, Filter.Default, ref allowedPlanes);
		var excludedPlanes = new MoverPlaneBuffer();
		CharacterMover.CollideMover(broadPhase, overlapTransform, capsule, excludedFilter, ref excludedPlanes);
		Check("character overlap-plane collection respects filtering", allowedPlanes.Count > 0 && excludedPlanes.Count == 0);

		ShapeOperations.SetFilter(wallShapeEntity, new Filter { CategoryBits = 2, MaskBits = 0, GroupIndex = -9 });
		var negativeGroup = CharacterMover.CastMover(broadPhase, FWorldTransform.Identity, capsule, translation, FP.One,
			new Filter { CategoryBits = 4, MaskBits = ulong.MaxValue, GroupIndex = -9 });
		ShapeOperations.SetFilter(wallShapeEntity, new Filter { CategoryBits = 2, MaskBits = 0, GroupIndex = 9 });
		var positiveGroup = CharacterMover.CastMover(broadPhase, FWorldTransform.Identity, capsule, translation, FP.One,
			new Filter { CategoryBits = 4, MaskBits = 0, GroupIndex = 9 });
		Check("character mover respects negative and positive collision groups", negativeGroup == FP.One && positiveGroup < FP.One);
		Shutdown();
	}

	private static void BulletCcdRollbackTest() {
		Console.WriteLine("--- BulletCcdRollbackTest ---");
		Bootstrap();
		var wall = W.NewEntity<Default>();
		BodyOperations.CreateBody(wall, BodyType.Static, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(3, 4), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(wall, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(1, 20), 2.ToFP(), 2.ToFP())));

		var continuousReceiver = W.RegisterEventReceiver<ContinuousHitEvent>();
		var bullet = W.NewEntity<Default>();
		BodyOperations.CreateBody(bullet, BodyType.Kinematic, FWorldTransform.Identity);
		BodyOperations.SetBullet(bullet, true);
		BodyOperations.SetLinearVelocity(bullet, new FVector3(60.ToFP(), FP.Zero, FP.Zero));
		var bulletShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 10));
		bulletShape.EnableContactEvents = true;
		ShapeFactory.CreateShape(bullet, bulletShape);
		var bulletGid = bullet.GID;

		var snapshot = W.Serializer.CreateWorldSnapshot();
		W.Tick();
		Systems.Update();
		if (!bulletGid.TryUnpack<TestWorld>(out var liveBullet)) {
			Check("bullet resolves after the live CCD tick", false);
			Shutdown();
			return;
		}
		Check("bullet resolves after the live CCD tick", true);
		var liveX = liveBullet.Read<Body>().Transform.Position.X;
		var liveState = W.Serializer.CreateWorldSnapshot();
		Check("supported-speed bullet does not tunnel through a thin wall", liveX > Fixed64.FP.Zero && liveX < Fixed64.FP.FromRatio(3, 4));
		var continuousCount = 0;
		var validContinuousEvent = false;
		foreach (var e in continuousReceiver) {
			continuousCount++;
			validContinuousEvent |= e.Value.Fraction > FP.Zero && e.Value.Fraction < FP.One && e.Value.Normal.X < FP.Zero;
		}
		Check("bullet CCD emits one oriented continuous hit event", continuousCount == 1 && validContinuousEvent);

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		W.Tick();
		Systems.Update();
		if (!bulletGid.TryUnpack<TestWorld>(out var replayBullet)) {
			Check("bullet resolves after rollback replay", false);
			Shutdown();
			return;
		}
		Check("bullet resolves after rollback replay", true);
		var replayX = replayBullet.Read<Body>().Transform.Position.X;
		var replayState = W.Serializer.CreateWorldSnapshot();
		Check("bullet CCD reproduces the same time of impact after rollback", replayX == liveX && Fnv1a64(replayState, replayState.Length) == Fnv1a64(liveState, liveState.Length));
		for (var tick = 0; tick < 5; tick++) {
			W.Tick();
			Systems.Update();
		}
		Check("surviving bullet does not drive through the wall after impact", bulletGid.TryUnpack<TestWorld>(out var stoppedBullet)
			&& stoppedBullet.Read<Body>().Transform.Position.X < Fixed64.FP.FromRatio(3, 4));
		Shutdown();
	}

	/// <summary>
	/// box3d's own downward "pogo stick" ray, ported verbatim from CharacterMover::SolveMove
	/// (samples/sample.cpp): true when grounded near the surface, false mid-air; also drives a
	/// spring-damper velocity (not just a boolean) that's nonzero once grounded and resets to zero
	/// while airborne.
	/// </summary>
	private static void PogoGroundingRayTest() {
		Console.WriteLine("--- PogoGroundingRayTest ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		var groundShape = Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP()));
		groundShape.Filter.CategoryBits = 2;
		ShapeFactory.CreateShape(groundBody, groundShape);

		// See RayCastHitsSphereCapsuleAndBoxTest's remarks: the ground's proxy doesn't exist until
		// ShapeProxySystem has run at least once.
		W.Tick();
		Systems.Update();

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var dt = Space.GameCore.Const.DeltaTime.To32();
		var hertz = FP.FromRatio(4, 1);
		var dampingRatio = FP.FromRatio(7, 10);

		// Ground top is at y=0.5 and pogoRestLength=capsule.Radius=0.5, so a mover sitting exactly at
		// y=0.5 is already at the spring's equilibrium (zero error, correctly zero pogoVelocity) --
		// start slightly below rest instead, so there's an actual correction to check for.
		var standingXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3, 10), Fixed64.FP.Zero), FQuaternion.Identity);
		var pogoVelocity = FP.Zero;
		Check("mover standing near the ground is grounded via the pogo ray", CharacterMover.UpdatePogoGrounding(broadPhase, standingXf, capsule, dt, hertz, dampingRatio, FP.Zero, FP.FromRatio(7071, 10000), ref pogoVelocity));
		Check("the pogo spring produces a nonzero corrective velocity when off its rest length", pogoVelocity != FP.Zero);
		var excludedPogoVelocity = FP.Zero;
		var excludedGround = new Filter { CategoryBits = ulong.MaxValue, MaskBits = 1, GroupIndex = 0 };
		Check("character ground probing respects filtering", !CharacterMover.UpdatePogoGrounding(broadPhase, standingXf, capsule, dt, hertz, dampingRatio,
			FP.Zero, FP.FromRatio(7071, 10000), excludedGround, ref excludedPogoVelocity));

		var airborneXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(50, 1), Fixed64.FP.Zero), FQuaternion.Identity);
		var airbornePogoVelocity = FP.Zero;
		Check("mover far above the ground is not grounded via the pogo ray", !CharacterMover.UpdatePogoGrounding(broadPhase, airborneXf, capsule, dt, hertz, dampingRatio, FP.Zero, FP.FromRatio(7071, 10000), ref airbornePogoVelocity));
		Check("pogo velocity resets to zero while airborne", airbornePogoVelocity == FP.Zero);

		Shutdown();
	}

	/// <summary>
	/// Diagnostic, not a pass/fail check: measures per-tick physics cost and what a rollback
	/// "catch-up burst" (Session.FastForwardToTick resimulating many ticks synchronously in one
	/// client frame, e.g. after a misprediction correction) actually costs in wall-clock time, to
	/// find out whether physics tick cost is a plausible cause of a client falling behind real time.
	/// </summary>
	private static void BenchResimulationCost() {
		Console.WriteLine("--- BenchResimulationCost ---");
		Bootstrap();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body { Type = BodyType.Dynamic, GravityScale = FP.One, Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(5, 1), Fixed64.FP.FromRatio(2, 1)), FQuaternion.Identity) });
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.Half);
		sphereShape.Density = FP.One;
		ShapeFactory.CreateShape(sphereBody, sphereShape);

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity) });
		ShapeFactory.CreateShape(boxBody, Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.Half, 2.ToFP())));

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var grounded = false;
		var pogoVelocity = FP.Zero;
		var jumpCooldown = FP.Zero;
		var gravity = new FVector3(FP.Zero, -FP.FromRatio(10, 1), FP.Zero);
		var dt = Space.GameCore.Const.DeltaTime.To32();
		var pogoHertz = FP.FromRatio(4, 1);
		var pogoDampingRatio = FP.FromRatio(7, 10);

		// Let the scene settle first, matching the real game's steady state (resting bodies, not free-falling).
		for (var tick = 0; tick < 120; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, ref pogoVelocity, ref jumpCooldown, capsule, FVector3.Zero, false, gravity, FP.FromRatio(8, 1), dt);
			W.Tick();
			Systems.Update();
		}

		const int sampleTicks = 600;

		var swPhysicsOnly = Stopwatch.StartNew();
		for (var tick = 0; tick < sampleTicks; tick++) {
			W.Tick();
			Systems.Update();
		}
		swPhysicsOnly.Stop();

		var swWithMoverNoRay = Stopwatch.StartNew();
		for (var tick = 0; tick < sampleTicks; tick++) {
			var target = moverXf.Position + dt * moverVelocity;
			var planes = new MoverPlaneBuffer();
			for (var iteration = 0; iteration < 5; iteration++) {
				planes.Clear();
				CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);
				var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
				var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
				delta *= fraction;
				moverXf.Position += delta;
				if (FVector3.LengthSqr(delta) < FP.FromRatio(1, 100) * FP.FromRatio(1, 100)) {
					break;
				}
			}
			CharacterMover.ApplyPushImpulses(moverXf, moverVelocity, in planes);
			W.Tick();
			Systems.Update();
		}
		swWithMoverNoRay.Stop();

		var swWithMoverAndRay = Stopwatch.StartNew();
		for (var tick = 0; tick < sampleTicks; tick++) {
			_ = CharacterMover.UpdatePogoGrounding(broadPhase, moverXf, capsule, dt, pogoHertz, pogoDampingRatio, FP.Zero, FP.FromRatio(7071, 10000), ref pogoVelocity);
			var target = moverXf.Position + dt * moverVelocity + dt * pogoVelocity * FVector3.Up;
			var planes = new MoverPlaneBuffer();
			for (var iteration = 0; iteration < 5; iteration++) {
				planes.Clear();
				CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);
				var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
				var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
				delta *= fraction;
				moverXf.Position += delta;
				if (FVector3.LengthSqr(delta) < FP.FromRatio(1, 100) * FP.FromRatio(1, 100)) {
					break;
				}
			}
			CharacterMover.ApplyPushImpulses(moverXf, moverVelocity, in planes);
			W.Tick();
			Systems.Update();
		}
		swWithMoverAndRay.Stop();

		Console.WriteLine($"  Systems.Update() alone:              {swPhysicsOnly.Elapsed.TotalMilliseconds / sampleTicks * 1000:F1} us/tick");
		Console.WriteLine($"  + full mover step (no grounded ray): {swWithMoverNoRay.Elapsed.TotalMilliseconds / sampleTicks * 1000:F1} us/tick");
		Console.WriteLine($"  + full mover step (with pogo ray):   {swWithMoverAndRay.Elapsed.TotalMilliseconds / sampleTicks * 1000:F1} us/tick");

		foreach (var burst in new[] { 30, 60, 120, 240 }) {
			var sw = Stopwatch.StartNew();
			for (var tick = 0; tick < burst; tick++) {
				_ = CharacterMover.UpdatePogoGrounding(broadPhase, moverXf, capsule, dt, pogoHertz, pogoDampingRatio, FP.Zero, FP.FromRatio(7071, 10000), ref pogoVelocity);
				var target = moverXf.Position + dt * moverVelocity + dt * pogoVelocity * FVector3.Up;
				var planes = new MoverPlaneBuffer();
				for (var iteration = 0; iteration < 5; iteration++) {
					planes.Clear();
					CharacterMover.CollideMover(broadPhase, moverXf, capsule, ref planes);
					var (delta, _) = MoverSolver.SolvePlanes(target - moverXf.Position, ref planes);
					var fraction = CharacterMover.CastMover(broadPhase, moverXf, capsule, delta, FP.One);
					delta *= fraction;
					moverXf.Position += delta;
					if (FVector3.LengthSqr(delta) < FP.FromRatio(1, 100) * FP.FromRatio(1, 100)) {
						break;
					}
				}
				CharacterMover.ApplyPushImpulses(moverXf, moverVelocity, in planes);
				W.Tick();
				Systems.Update();
			}
			sw.Stop();
			Console.WriteLine($"  resim burst of {burst,3} ticks: {sw.Elapsed.TotalMilliseconds:F2} ms total ({sw.Elapsed.TotalMilliseconds / burst:F3} ms/tick) -- {(sw.Elapsed.TotalMilliseconds > 16.67 ? "EXCEEDS a 60fps frame budget" : "within a 60fps frame budget")}");
		}

		Shutdown();
	}

	/// <summary>
	/// Diagnostic: measures the cost of GameInterpolationReceiver.SaveInterpolationState's full
	/// world serialize + full world deserialize-into-a-second-parallel-world -- unlike physics
	/// resimulation, this runs unconditionally once per simulated tick (every ~16.67ms in steady
	/// state on the client, not just during a rollback burst), so any real cost here is a
	/// guaranteed, recurring per-frame tax rather than something that only shows up rarely.
	/// </summary>
	private static void BenchInterpolationSnapshotCost() {
		Console.WriteLine("--- BenchInterpolationSnapshotCost ---");
		Bootstrap();

		WPrev.Create(GameWorldSetup.WorldConfig);
		WPrev.Types().RegisterAll(typeof(CoreRoot).Assembly);
		WPrev.Initialize();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity) });
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body { Type = BodyType.Dynamic, GravityScale = FP.One, Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(5, 1), Fixed64.FP.FromRatio(2, 1)), FQuaternion.Identity) });
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.Half);
		sphereShape.Density = FP.One;
		ShapeFactory.CreateShape(sphereBody, sphereShape);

		var boxBody = W.NewEntity<Default>();
		boxBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1)), FQuaternion.Identity) });
		ShapeFactory.CreateShape(boxBody, Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.Half, 2.ToFP())));

		var playerEntity = W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });

		for (var tick = 0; tick < 60; tick++) {
			W.Tick();
			Systems.Update();
		}

		var buffer = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);

		const int iterations = 1000;
		var sw = Stopwatch.StartNew();
		for (var i = 0; i < iterations; i++) {
			buffer.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref buffer);
			var reader = buffer.AsReader();
			WPrev.Serializer.LoadWorldSnapshot(ref reader, true);
		}
		sw.Stop();

		var usPerCall = sw.Elapsed.TotalMilliseconds / iterations * 1000;
		Console.WriteLine($"  full-world serialize + deserialize-into-second-world: {usPerCall:F1} us/call, entities: {W.Query<All<ViewId>>().EntitiesCount()}, buffer capacity: {GameWorldRollback.WorldSnapshotLength / 1024} KB");
		Console.WriteLine($"  at 60Hz this recurs every tick unconditionally -- {usPerCall / 1000.0 * TickRate:F2} ms/s of budget, vs a 16.67ms/frame target");

		WPrev.Destroy();
		Shutdown();
	}

	private static void KinematicProjectileKillsDummyTest() {
		Console.WriteLine("--- KinematicProjectileKillsDummyTest ---");
		Bootstrap();

		var dummy = W.NewEntity<Dummy>();
		BodyOperations.CreateBody(dummy, BodyType.Kinematic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var dummyShape = Shape.MakeCapsule(
			new FVector3(FP.Zero, -FP.Half, FP.Zero),
			new FVector3(FP.Zero, FP.Half, FP.Zero),
			FP.Half);
		dummyShape.EnableContactEvents = true;
		ShapeFactory.CreateShape(dummy, dummyShape);
		var dummyGid = dummy.GID;

		var projectile = W.NewEntity<Projectile>();
		BodyOperations.CreateBody(projectile, BodyType.Kinematic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var projectileShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 4));
		projectileShape.EnableContactEvents = true;
		ShapeFactory.CreateShape(projectile, projectileShape);
		var projectileGid = projectile.GID;

		W.Tick();
		Systems.Update();
		W.Tick();
		Systems.Update();

		Check("kinematic projectile is destroyed after touching a kinematic dummy", !projectileGid.TryUnpack<TestWorld>(out _));
		Check("kinematic dummy dies after being hit by a projectile", !dummyGid.TryUnpack<TestWorld>(out _));

		Shutdown();
	}

	private static void RejectedBroadPhasePairsTest() {
		Console.WriteLine("--- RejectedBroadPhasePairsTest ---");
		Bootstrap();

		var sameBody = W.NewEntity<Default>();
		sameBody.Set(new Body {
			Type = BodyType.Dynamic,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		var sphere = Shape.MakeSphere(FVector3.Zero, FP.One);
		sphere.Density = FP.One;
		ShapeFactory.CreateShape(sameBody, sphere);
		ShapeFactory.CreateShape(sameBody, sphere);

		var filteredStatic = W.NewEntity<Default>();
		filteredStatic.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(20, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(filteredStatic, Shape.MakeSphere(FVector3.Zero, FP.One));

		var filteredDynamic = W.NewEntity<Default>();
		filteredDynamic.Set(new Body {
			Type = BodyType.Dynamic,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(20, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var filteredShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		filteredShape.Density = FP.One;
		filteredShape.Filter.MaskBits = 0;
		ShapeFactory.CreateShape(filteredDynamic, filteredShape);

		for (var i = 0; i < 2; i++) {
			var staticBody = W.NewEntity<Default>();
			staticBody.Set(new Body {
				Type = BodyType.Static,
				Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(40, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			});
			ShapeFactory.CreateShape(staticBody, Shape.MakeSphere(FVector3.Zero, FP.One));

			var kinematicBody = W.NewEntity<Default>();
			kinematicBody.Set(new Body {
				Type = BodyType.Kinematic,
				Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(60, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			});
			ShapeFactory.CreateShape(kinematicBody, Shape.MakeSphere(FVector3.Zero, FP.One));
		}

		W.Tick();
		Systems.Update();

		var counts = PhysicsDiagnostics.Capture();
		Check("same-body, filtered, and non-event static/static or kinematic/kinematic pairs create no contacts", counts.Contacts == 0);
		Check("rejected pairs are immediately released from the broad-phase cache", counts.CachedPairs == 0);

		var valid = true;
		try {
			W.GetResource<BroadPhase>().Validate();
		} catch (Exception e) {
			valid = false;
			Console.WriteLine($"  validation threw: {e}");
		}
		Check("broad-phase validation accepts consistent rejected-pair state", valid);

		Shutdown();
	}

	private static void BodyTransformMutationTest() {
		Console.WriteLine("--- BodyTransformMutationTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var bodyEntity = W.NewEntity<Default>();
		BodyOperations.CreateBody(bodyEntity, BodyType.Dynamic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var shape = Shape.MakeSphere(new FVector3(FP.One, FP.Zero, FP.Zero), FP.One);
		shape.Density = FP.One;
		var shapeEntity = ShapeFactory.CreateShape(bodyEntity, shape);
		W.Tick();
		Systems.Update();

		var destination = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(20, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity);
		BodyOperations.SetTransform(bodyEntity, destination);
		ref readonly var moved = ref bodyEntity.Read<Body>();
		Check("teleport updates body origin and center of mass atomically",
			moved.Transform.Position.X == Fixed64.FP.FromRatio(20, 1)
			&& Math.Abs(Fixed64.FConversions.ToDouble(moved.Center.X) - 21.0) < 0.001);

		W.Tick();
		Systems.Update();
		ref readonly var finalized = ref bodyEntity.Read<Body>();
		Check("teleported dynamic body does not snap back during solver finalization", finalized.Transform.Position.X == Fixed64.FP.FromRatio(20, 1));
		var hit = PhysicsQueries.CastRayClosest(
			W.GetResource<BroadPhase>(),
			new FPos(Fixed64.FP.FromRatio(21, 1), Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero),
			new FVector3(FP.Zero, -10.ToFP(), FP.Zero),
			Filter.Default,
			out var result);
		Check("teleport updates the broad-phase proxy", hit && result.Shape == shapeEntity.GID);
		W.GetResource<BroadPhase>().Validate();
		Shutdown();
	}

	private static void StaticTransformMutationTest() {
		Console.WriteLine("--- StaticTransformMutationTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var staticShapeData = Shape.MakeSphere(FVector3.Zero, FP.One);
		staticShapeData.Filter.CategoryBits = 2;
		var staticShape = ShapeFactory.CreateShape(staticBody, staticShapeData);

		var oldDynamic = W.NewEntity<Default>();
		BodyOperations.CreateBody(oldDynamic, BodyType.Dynamic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var dynamicShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		dynamicShape.Density = FP.One;
		dynamicShape.Filter.CategoryBits = 1;
		ShapeFactory.CreateShape(oldDynamic, dynamicShape);

		var newDynamic = W.NewEntity<Default>();
		BodyOperations.CreateBody(newDynamic, BodyType.Dynamic, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(newDynamic, dynamicShape);

		W.Tick();
		Systems.Update();
		Check("static fixture begins with a contact at its old location", W.Query<All<Contact>>().EntitiesCount() == 1);

		BodyOperations.SetTransform(staticBody, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		Check("moving a static body immediately invalidates stale contacts", W.Query<All<Contact>>().EntitiesCount() == 0);
		W.Tick();
		Systems.Update();
		Check("moved static body creates contacts at its new location", W.Query<All<Contact>>().EntitiesCount() == 1);

		var staticOnlyFilter = new Filter { CategoryBits = ulong.MaxValue, MaskBits = 2 };
		var oldRayHits = PhysicsQueries.CastRayClosest(W.GetResource<BroadPhase>(), new FPos(Fixed64.FP.Zero, 5.ToFP().To64(), Fixed64.FP.Zero), new FVector3(FP.Zero, -10.ToFP(), FP.Zero), staticOnlyFilter, out var oldHit);
		var newRayHits = PhysicsQueries.CastRayClosest(W.GetResource<BroadPhase>(), new FPos(Fixed64.FP.FromRatio(10, 1), 5.ToFP().To64(), Fixed64.FP.Zero), new FVector3(FP.Zero, -10.ToFP(), FP.Zero), staticOnlyFilter, out var newHit);
		Check("moved static body leaves its old query location", !oldRayHits || oldHit.Shape != staticShape.GID);
		Check("moved static body enters its new query location", newRayHits && newHit.Shape == staticShape.GID);
		W.GetResource<BroadPhase>().Validate();
		Shutdown();
	}

	private static void BodyTypeAndEnabledMutationTest() {
		Console.WriteLine("--- BodyTypeAndEnabledMutationTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(ground, Shape.MakeSphere(FVector3.Zero, FP.One));
		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var shape = Shape.MakeSphere(FVector3.Zero, FP.One);
		shape.Density = FP.One;
		ShapeFactory.CreateShape(body, shape);
		W.Tick();
		Systems.Update();

		BodyOperations.SetType(body, BodyType.Dynamic);
		W.GetResource<BroadPhase>().Validate();
		Check("body-type change recomputes dynamic mass", body.Read<Body>().Mass > FP.Zero && body.Read<Body>().InvMass > FP.Zero);
		W.Tick();
		Systems.Update();
		Check("body-type change preserves the proxy and creates newly eligible contacts", PhysicsDiagnostics.Capture() is { Proxies: 2, Contacts: 1, CachedPairs: 1 });

		BodyOperations.Disable(body);
		Check("disabling a body removes its proxy, contacts, and pair state", PhysicsDiagnostics.Capture() is { Proxies: 1, Contacts: 0, CachedPairs: 0 });
		W.Tick();
		Systems.Update();
		Check("disabled body stays out of the broad phase", PhysicsDiagnostics.Capture().Proxies == 1);

		BodyOperations.Enable(body);
		W.Tick();
		Systems.Update();
		Check("enabling a body restores its proxy and eligible contacts", PhysicsDiagnostics.Capture() is { Proxies: 2, Contacts: 1, CachedPairs: 1 });
		BodyOperations.SetType(body, BodyType.Kinematic);
		Check("changing away from dynamic clears mass and migrates the proxy", body.Read<Body>().Mass == FP.Zero && body.Read<Body>().InvMass == FP.Zero);
		W.GetResource<BroadPhase>().Validate();
		Shutdown();
	}

	private static void ShapeFilterMutationTest() {
		Console.WriteLine("--- ShapeFilterMutationTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(staticBody, Shape.MakeSphere(FVector3.Zero, FP.One));
		var dynamicBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(dynamicBody, BodyType.Dynamic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var dynamicShapeData = Shape.MakeSphere(FVector3.Zero, FP.One);
		dynamicShapeData.Density = FP.One;
		var dynamicShape = ShapeFactory.CreateShape(dynamicBody, dynamicShapeData);
		W.Tick();
		Systems.Update();
		Check("compatible live filters establish a contact", W.Query<All<Contact>>().EntitiesCount() == 1);
		var originalContact = default(EntityGID);
		foreach (var contact in W.Query<All<Contact>>().Entities()) {
			originalContact = contact.GID;
		}

		ShapeOperations.SetFilter(dynamicShape, new Filter { CategoryBits = 2, MaskBits = ulong.MaxValue });
		Check("compatible filter changes retain the existing contact and its warm-start state",
			originalContact.TryUnpack<TestWorld>(out _) && PhysicsDiagnostics.Capture() is { Contacts: 1, CachedPairs: 1 });

		ShapeOperations.SetFilter(dynamicShape, new Filter { CategoryBits = 2, MaskBits = 0 });
		Check("filter change immediately removes ineligible contacts and pairs", PhysicsDiagnostics.Capture() is { Contacts: 0, CachedPairs: 0 });
		W.Tick();
		Systems.Update();
		Check("incompatible filter remains contact-free without movement", W.Query<All<Contact>>().EntitiesCount() == 0);

		ShapeOperations.SetFilter(dynamicShape, Filter.Default);
		Check("eligible filter change creates a contact immediately", PhysicsDiagnostics.Capture() is { Contacts: 1, CachedPairs: 1 });
		W.Tick();
		Systems.Update();
		Check("filter-created contact remains valid without movement", PhysicsDiagnostics.Capture() is { Contacts: 1, CachedPairs: 1 });

		dynamicShape.Ref<Shape>().Filter.MaskBits = 0;
		W.Tick();
		Systems.Update();
		Check("existing contacts re-evaluate live filter state deterministically", PhysicsDiagnostics.Capture() is { Contacts: 0, CachedPairs: 0 });
		W.GetResource<BroadPhase>().Validate();
		Shutdown();
	}

	private static void ShapeGeometryDensityAndForcesTest() {
		Console.WriteLine("--- ShapeGeometryDensityAndForcesTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Dynamic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var shapeData = Shape.MakeSphere(FVector3.Zero, FP.One);
		shapeData.Density = FP.One;
		var shape = ShapeFactory.CreateShape(body, shapeData);
		W.Tick();
		Systems.Update();
		var originalMass = body.Read<Body>().Mass;

		ShapeOperations.SetDensity(shape, 2.ToFP());
		Check("density change updates body mass and inertia", body.Read<Body>().Mass == 2 * originalMass && body.Read<Body>().Inertia != FMatrix3.Zero);
		ShapeOperations.SetSphere(shape, new Sphere(new FVector3(FP.One, FP.Zero, FP.Zero), FP.FromRatio(5, 4)));
		Check("geometry change updates centroid, mass, and center of mass",
			shape.Read<Shape>().LocalCentroid == new FVector3(FP.One, FP.Zero, FP.Zero)
			&& body.Read<Body>().Mass > 2 * originalMass
			&& Math.Abs(Fixed64.FConversions.ToDouble(body.Read<Body>().Center.X) - 1.0) < 0.001);
		W.GetResource<BroadPhase>().Validate();

		BodyOperations.SetTransform(body, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(20, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		BodyOperations.SetVelocity(body, FVector3.Zero, FVector3.Zero);
		BodyOperations.ApplyLinearImpulseToCenter(body, new FVector3(body.Read<Body>().Mass, FP.Zero, FP.Zero));
		Check("linear impulse changes velocity through inverse mass", Math.Abs(body.Read<Body>().LinearVelocity.X.ToDouble() - 1.0) < 0.01);
		BodyOperations.ApplyAngularImpulse(body, new FVector3(FP.Zero, FP.One, FP.Zero));
		Check("angular impulse changes angular velocity through inverse inertia", body.Read<Body>().AngularVelocity.Y != FP.Zero);
		BodyOperations.SetAngularVelocity(body, new FVector3(FP.Zero, FP.Zero, FP.One));
		Check("angular velocity setter updates an enabled non-static body", body.Read<Body>().AngularVelocity.Z == FP.One);

		BodyOperations.SetVelocity(body, FVector3.Zero, FVector3.Zero);
		body.Ref<Body>().IsAwake = false;
		BodyOperations.ApplyLinearImpulse(body, new FVector3(body.Read<Body>().Mass, FP.Zero, FP.Zero), body.Read<Body>().Center + FVector3.Up);
		Check("point impulse changes linear and angular velocity and wakes the body",
			body.Read<Body>().LinearVelocity.X > FP.Zero
			&& body.Read<Body>().AngularVelocity.Z != FP.Zero
			&& body.Read<Body>().IsAwake);

		BodyOperations.SetVelocity(body, FVector3.Zero, FVector3.Zero);
		BodyOperations.ApplyForceToCenter(body, new FVector3(body.Read<Body>().Mass * 60.ToFP(), FP.Zero, FP.Zero));
		BodyOperations.ApplyForce(body, new FVector3(FP.One, FP.Zero, FP.Zero), body.Read<Body>().Center + FVector3.Up);
		BodyOperations.ApplyTorque(body, new FVector3(FP.Zero, FP.One, FP.Zero));
		W.Tick();
		Systems.Update();
		var velocityAfterForce = body.Read<Body>().LinearVelocity.X;
		Check("point forces and torques change angular velocity", body.Read<Body>().AngularVelocity.Y != FP.Zero && body.Read<Body>().AngularVelocity.Z != FP.Zero);
		W.Tick();
		Systems.Update();
		Check("force is integrated for one full tick and then cleared", velocityAfterForce > FP.Zero && body.Read<Body>().LinearVelocity.X == velocityAfterForce);
		W.GetResource<BroadPhase>().Validate();
		Shutdown();
	}

	private static void MutationRollbackTest() {
		Console.WriteLine("--- MutationRollbackTest ---");
		Bootstrap();
		W.GetResource<PhysicsWorld>().Gravity = FVector3.Zero;

		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Dynamic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var shapeData = Shape.MakeSphere(FVector3.Zero, FP.One);
		shapeData.Density = FP.One;
		var shape = ShapeFactory.CreateShape(body, shapeData);
		W.Tick();
		Systems.Update();

		var bodyGid = body.GID;
		var shapeGid = shape.GID;
		var snapshot = W.Serializer.CreateWorldSnapshot();
		var hashWriter = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		ulong HashState() {
			hashWriter.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref hashWriter);
			return Fnv1a64(hashWriter.Buffer, (int)hashWriter.Position);
		}

		static void ApplyMutations(W.Entity bodyEntity, W.Entity shapeEntity) {
			BodyOperations.SetTransform(bodyEntity, new FWorldTransform(new FPos(Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
			BodyOperations.SetType(bodyEntity, BodyType.Kinematic);
			BodyOperations.SetType(bodyEntity, BodyType.Dynamic);
			ShapeOperations.SetFilter(shapeEntity, new Filter { CategoryBits = 4, MaskBits = ulong.MaxValue });
			ShapeOperations.SetSphere(shapeEntity, new Sphere(new FVector3(FP.Half, FP.Zero, FP.Zero), FP.FromRatio(5, 4)));
			ShapeOperations.SetDensity(shapeEntity, 2.ToFP());
			BodyOperations.Disable(bodyEntity);
			BodyOperations.Enable(bodyEntity);
			BodyOperations.ApplyForceToCenter(bodyEntity, new FVector3(10.ToFP(), FP.Zero, FP.Zero));
		}

		ApplyMutations(body, shape);
		W.Tick();
		Systems.Update();
		var liveHash = HashState();
		var liveCounts = PhysicsDiagnostics.Capture();
		W.GetResource<BroadPhase>().Validate();

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		var bodyResolved = bodyGid.TryUnpack<TestWorld>(out var replayBody);
		var shapeResolved = shapeGid.TryUnpack<TestWorld>(out var replayShape);
		var resolved = bodyResolved && shapeResolved;
		Check("mutation rollback restores body and shape identities", resolved);
		if (resolved) {
			ApplyMutations(replayBody, replayShape);
			W.Tick();
			Systems.Update();
			Check("mutation replay reproduces physics counts", PhysicsDiagnostics.Capture() == liveCounts);
			Check("mutation replay reproduces the full state hash", HashState() == liveHash);
			W.GetResource<BroadPhase>().Validate();
		}

		Shutdown();
	}

	/// <summary>
	/// Missed projectiles used to live forever (nothing destroyed them except a hit). Verifies the
	/// <see cref="Lifetime"/> countdown keeps them alive mid-flight and then routes expiry through
	/// <see cref="DeadEvent"/> → <c>DeathSystem</c>'s complete body/shape/proxy teardown.
	/// </summary>
	private static void ProjectileDespawnTtlTest() {
		Console.WriteLine("--- ProjectileDespawnTtlTest ---");
		Bootstrap();

		var baseline = PhysicsDiagnostics.Capture();

		var projectile = W.NewEntity<Projectile>();
		BodyOperations.CreateBody(projectile, BodyType.Kinematic, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		var shape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 4));
		shape.EnableContactEvents = true;
		ShapeFactory.CreateShape(projectile, shape);
		projectile.Set(new Lifetime { TimeRemaining = Fixed64.FP.FromRatio(2, 1) });
		var projectileGid = projectile.GID;

		for (var i = 0; i < 60; i++) {
			W.Tick();
			Systems.Update();
		}
		Check("projectile is still alive at half its lifetime", projectileGid.TryUnpack<TestWorld>(out _) && PhysicsDiagnostics.Capture().Bodies == baseline.Bodies + 1);

		for (var i = 0; i < 70; i++) {
			W.Tick();
			Systems.Update();
		}
		Check("expired projectile entity is destroyed", !projectileGid.TryUnpack<TestWorld>(out _));
		Check("expiry returns every physics count to baseline", PhysicsDiagnostics.Capture() == baseline);
		W.GetResource<BroadPhase>().Validate();

		Shutdown();
	}

	/// <summary>
	/// Locks down the protocol behind the "can't shoot anymore after lots of shooting" bug, against
	/// the real session input path (<see cref="ShootSystem"/> included, no Godot): a render frame's
	/// idle input claims tick T; a same-tick rewrite of that input is rejected as
	/// <see cref="SetResult.Duplicate"/> and its data silently discarded -- a one-frame flick died
	/// there. The retry at the next tick applies and the shot spawns. ClientGame's input loop
	/// implements exactly that retry.
	/// </summary>
	private static void SessionInputDuplicateRetryTest() {
		Console.WriteLine("--- SessionInputDuplicateRetryTest ---");
		S.Create(SimulationType.ForwardOnly, GameSessionSetup.SessionConfig);
		// Client<T>.Create normally registers these; this test drives Session directly.
		S.Types().Signal<PlayerConnectedSignal>().Signal<PlayerDisconnectedSignal>();
		GameSessionSetup.Register();
		S.Initialize();
		GameWorldSetup.CreateAndInitialize();
		Systems.GetResource<CharacterRes>().AttackDelay = Space.GameCore.Const.DeltaTime * 3;

		W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = 0 });
		S.FastForwardToTick(2);

		var idle = new PlayerInput();
		var firstWrite = S.SetPredictionInput(channel: 0, idle);
		var flick = new PlayerInput { AttackX = Fixed64.FP.One };
		var sameTickRewrite = S.SetPredictionInput(channel: 0, flick);
		Check("first input write of a tick applies", firstWrite == SetResult.Applied);
		Check("a second write to the same tick is rejected as Duplicate", sameTickRewrite == SetResult.Duplicate);
		Check("the rejected write's data is discarded -- an unretried flick is lost for good", S.GetInput<PlayerInput>(channel: 0).Data.AttackX == Fixed64.FP.Zero);

		S.FastForwardToTick(S.CurrentTick + 1);
		var retry = S.SetPredictionInput(channel: 0, flick);
		Check("retrying the flick at the next tick applies", retry == SetResult.Applied);

		S.FastForwardToTick(S.CurrentTick + 1);
		Check("the retried flick does not spawn before its attack delay", W.Query<All<IsProjectile>>().EntitiesCount() == 0);
		S.FastForwardToTick(S.CurrentTick + 1);
		Check("the projectile remains queued until the final delay tick", W.Query<All<IsProjectile>>().EntitiesCount() == 0);
		S.FastForwardToTick(S.CurrentTick + 1);
		Check("the retried flick spawns after its attack delay", W.Query<All<IsProjectile>>().EntitiesCount() == 1);

		GameWorldSetup.Destroy();
		S.Destroy();
	}

	private static void BodyDestructionRollbackTest() {
		Console.WriteLine("--- BodyDestructionRollbackTest ---");
		Bootstrap();

		var ground = W.NewEntity<Default>();
		ground.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(ground, Shape.MakeSphere(FVector3.Zero, 5.ToFP()));

		var body = W.NewEntity<Default>();
		body.Set(new Body {
			Type = BodyType.Dynamic,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		var shape = Shape.MakeSphere(new FVector3(FP.Half, FP.Zero, FP.Zero), FP.One);
		shape.Density = FP.One;
		ShapeFactory.CreateShape(body, shape);
		shape.SphereShape.Center.X = -FP.Half;
		ShapeFactory.CreateShape(body, shape);
		var bodyGid = body.GID;

		W.Tick();
		Systems.Update();
		var before = PhysicsDiagnostics.Capture();
		Check("multi-shape body creates one contact per overlapping shape", before.Contacts == 2 && before.CachedPairs == 2);

		var snapshot = W.Serializer.CreateWorldSnapshot();
		var hashWriter = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		ulong HashState() {
			hashWriter.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref hashWriter);
			return Fnv1a64(hashWriter.Buffer, (int)hashWriter.Position);
		}

		var liveBodyResolved = bodyGid.TryUnpack<TestWorld>(out var liveBody);
		Check("body resolves before live destruction", liveBodyResolved);
		if (!liveBodyResolved) {
			Shutdown();
			return;
		}
		PhysicsBodyLifecycle.DestroyBody(liveBody);
		W.Tick();
		Systems.Update();
		var liveCounts = PhysicsDiagnostics.Capture();
		var liveHash = HashState();
		W.GetResource<BroadPhase>().Validate();

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		var replayBodyResolved = bodyGid.TryUnpack<TestWorld>(out var replayBody);
		Check("body GID resolves after rollback restore", replayBodyResolved);
		if (!replayBodyResolved) {
			Shutdown();
			return;
		}
		PhysicsBodyLifecycle.DestroyBody(replayBody);
		W.Tick();
		Systems.Update();
		var replayCounts = PhysicsDiagnostics.Capture();
		var replayHash = HashState();
		W.GetResource<BroadPhase>().Validate();

		Check("body destruction removes every owned shape, proxy, contact, and pair", liveCounts == new PhysicsCounts(1, 1, 1, 0, 0, 0));
		Check("rollback across body destruction reproduces physics counts", replayCounts == liveCounts);
		Check("rollback across body destruction reproduces the full state hash", replayHash == liveHash);

		Shutdown();
	}

	private static void DeathSystemPhysicsLifecycleTest() {
		Console.WriteLine("--- DeathSystemPhysicsLifecycleTest ---");
		Bootstrap();

		var ground = W.NewEntity<Default>();
		ground.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(ground, Shape.MakeSphere(FVector3.Zero, 5.ToFP()));
		W.Tick();
		Systems.Update();
		var baseline = PhysicsDiagnostics.Capture();

		var projectile = W.NewEntity<Default>();
		projectile.Set(new Body {
			Type = BodyType.Dynamic,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		var shape = Shape.MakeSphere(FVector3.Zero, FP.One);
		shape.Density = FP.One;
		ShapeFactory.CreateShape(projectile, shape);
		var projectileGid = projectile.GID;
		W.Tick();
		Systems.Update();

		W.SendEvent(new DeadEvent { Gid = projectileGid });
		Systems.Update();
		var counts = PhysicsDiagnostics.Capture();
		Check("DeathSystem routes physics entities through complete body teardown", counts == baseline);
		W.GetResource<BroadPhase>().Validate();

		Shutdown();
	}

	/// <summary>Phase 1 lifecycle acceptance check over 10,000 production body teardowns.</summary>
	private static void ProjectileLifecycleCountsTest() {
		Console.WriteLine("--- ProjectileLifecycleCountsTest ---");
		Bootstrap();

		var broadPhase = W.GetResource<BroadPhase>();

		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(FPos.Zero, FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

		W.Tick();
		Systems.Update();

		var baseline = PhysicsDiagnostics.Capture();
		Console.WriteLine($"  baseline: {baseline}");

		const int cycles = 10_000;
		var leakedAtCycle = -1;
		var leakedCounts = default(PhysicsCounts);
		for (var cycle = 0; cycle < cycles; cycle++) {
			// Spawn production-like (ShootSystem): a small sphere on its own body, slightly overlapping
			// the ground's surface so a broad-phase pair, contact, and touch event form on the first update.
			var projectile = W.NewEntity<Default>();
			projectile.Set(new Body {
				Type = BodyType.Dynamic,
				GravityScale = FP.One,
				Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(3, 5), Fixed64.FP.Zero), FQuaternion.Identity),
			});
			var shape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(1, 4));
			shape.Density = FP.One;
			ShapeFactory.CreateShape(projectile, shape);
			var projectileGid = projectile.GID;

			W.Tick();
			Systems.Update();

			if (!projectileGid.TryUnpack<TestWorld>(out var liveProjectile)) {
				leakedAtCycle = cycle;
				leakedCounts = PhysicsDiagnostics.Capture();
				break;
			}
			PhysicsBodyLifecycle.DestroyBody(liveProjectile);

			W.Tick();
			Systems.Update();

			// Checked every cycle (cheap) so a transient leak can't hide between checkpoints; reported
			// only at checkpoints or on the first divergence to keep the log readable.
			if (PhysicsDiagnostics.Capture() != baseline) {
				if (leakedAtCycle == -1) {
					leakedAtCycle = cycle;
					leakedCounts = PhysicsDiagnostics.Capture();
				}
			}

			if (cycle % 1000 == 999) {
				var counts = PhysicsDiagnostics.Capture();
				Check($"physics counts stay at baseline after {cycle + 1} spawn/destroy cycles ({counts})", counts == baseline && leakedAtCycle == -1);
			}
		}

		var finalCounts = PhysicsDiagnostics.Capture();
		var leakDetail = leakedAtCycle >= 0 ? $" (first leak at cycle {leakedAtCycle}: {leakedCounts})" : "";
		Check($"physics counts match baseline after every one of the {cycles} cycles{leakDetail}", leakedAtCycle == -1 && finalCounts == baseline);
		broadPhase.Validate();

		Shutdown();
	}

	private static void FixedPointBoundaryValidationTest() {
		Console.WriteLine("--- FixedPointBoundaryValidationTest ---");
		Bootstrap();

		var boundaryBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(boundaryBody, BodyType.Kinematic,
			new FWorldTransform(new FPos(Fixed64.FP.FromRatio(8192, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity));
		Check("the documented coordinate boundary is accepted", boundaryBody.Has<Body>());

		var outsideBody = W.NewEntity<Default>();
		Check("coordinates outside the supported envelope fail before body creation", Throws<ArgumentOutOfRangeException>(() =>
			BodyOperations.CreateBody(outsideBody, BodyType.Dynamic,
				new FWorldTransform(new FPos(Fixed64.FP.FromRatio(8193, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity)))
			&& !outsideBody.Has<Body>());

		var staticBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(staticBody, BodyType.Static, FWorldTransform.Identity);
		ShapeFactory.CreateShape(staticBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));
		var staticShapeCount = W.Query<All<Shape>>().EntitiesCount();
		Check("static dimensions beyond the documented boundary are rejected without creating a shape", Throws<ArgumentOutOfRangeException>(() =>
			ShapeFactory.CreateShape(staticBody, Shape.MakeBox(FVector3.Zero, new FVector3(FP.FromRatio(4001, 100), FP.Half, FP.One))))
			&& W.Query<All<Shape>>().EntitiesCount() == staticShapeCount);

		var dynamicBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(dynamicBody, BodyType.Dynamic, FWorldTransform.Identity);
		var validShape = Shape.MakeSphere(FVector3.Zero, 3.ToFP());
		var shapeEntity = ShapeFactory.CreateShape(dynamicBody, validShape);
		Check("validated default density is one", shapeEntity.Read<Shape>().Density == FP.One);
		Check("mass/inertia overflow candidates fail before replacing live geometry", Throws<ArgumentOutOfRangeException>(() =>
			ShapeOperations.SetSphere(shapeEntity, new Sphere(FVector3.Zero, 4.ToFP())))
			&& shapeEntity.Read<Shape>().SphereShape.Radius == 3.ToFP());
		Check("density above the validated range is rejected transactionally", Throws<ArgumentOutOfRangeException>(() =>
			ShapeOperations.SetDensity(shapeEntity, FP.FromRatio(201, 100)))
			&& shapeEntity.Read<Shape>().Density == FP.One);

		BodyOperations.SetLinearVelocity(dynamicBody, new FVector3(60.ToFP(), FP.Zero, FP.Zero));
		Check("the supported linear-speed boundary is accepted", dynamicBody.Read<Body>().LinearVelocity.X == 60.ToFP());
		BodyOperations.SetLinearVelocity(dynamicBody, new FVector3(61.ToFP(), FP.Zero, FP.Zero));
		Check("linear speed beyond the configured maximum is clamped like the solver clamps it",
			NearlyEqual(FVector3.Length(dynamicBody.Read<Body>().LinearVelocity), 60.ToFP()));
		var solverAngularLimit = B3Config.MaxRotation * Space.GameCore.Const.InvDeltaTime.To32();
		BodyOperations.SetAngularVelocity(dynamicBody, new FVector3(FP.Zero, FP.Zero, 100.ToFP()));
		Check("angular speed beyond the solver's limit is clamped to it",
			NearlyEqual(FVector3.Length(dynamicBody.Read<Body>().AngularVelocity), solverAngularLimit));
		// 40 rad/s is above the old 30 rad/s API bound but below the solver's own ~47 rad/s clamp, so the
		// solver can produce it; a small impulse on such a body must not throw.
		BodyOperations.SetAngularVelocity(dynamicBody, new FVector3(FP.Zero, FP.Zero, 40.ToFP()));
		BodyOperations.ApplyLinearImpulse(dynamicBody, new FVector3(FP.Zero, FP.One, FP.Zero),
			dynamicBody.Read<Body>().Center + new FVector3(FP.One, FP.Zero, FP.Zero));
		Check("impulses on a body spinning at a solver-reachable speed are accepted",
			FVector3.Length(dynamicBody.Read<Body>().AngularVelocity) <= solverAngularLimit + FP.CalculationsEpsilon);
		BodyOperations.ApplyLinearImpulseToCenter(dynamicBody, new FVector3(30000.ToFP(), FP.Zero, FP.Zero));
		Check("an impulse far beyond the speed limit saturates instead of wrapping",
			dynamicBody.Read<Body>().LinearVelocity.X > FP.Zero
			&& NearlyEqual(FVector3.Length(dynamicBody.Read<Body>().LinearVelocity), 60.ToFP()));

		var thinCapsule = Shape.MakeCapsule(new FVector3(FP.Zero, -2.ToFP(), FP.Zero), new FVector3(FP.Zero, 2.ToFP(), FP.Zero), FP.FromRatio(4, 100));
		var thinBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(thinBody, BodyType.Dynamic, new FWorldTransform(new FPos(Fixed64.FP.Zero, 20.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		// About its long axis this capsule's inertia (~1.6e-5) is one Q16.16 ulp; its exact inverse (~62000)
		// would overflow Q16.16. The inertia floor must keep it rotatable with a bounded inverse.
		ShapeFactory.CreateShape(thinBody, thinCapsule);
		ref readonly var thin = ref thinBody.Read<Body>();
		var maxInverse = PhysicsValidation.MaximumInverseMassOrInertia + FP.FromRatio(1, 100);
		Check("a thin dynamic capsule gets a floored, invertible inertia instead of overflowing",
			thin.InvInertiaLocal.Cy.Y > FP.Zero && thin.InvInertiaLocal.Cy.Y <= maxInverse
			&& thin.InvInertiaLocal.Cx.X > FP.Zero && thin.InvInertiaLocal.Cz.Z > FP.Zero);
		// The same thin capsule laid diagonally and offset from the body origin: its tiny long-axis moment
		// hides in off-diagonal terms, so a floor based on the smallest diagonal entry would miss it.
		var obliqueCapsule = Shape.MakeCapsule(new FVector3(FP.Zero, -FP.One, FP.One), new FVector3(2.ToFP(), FP.One, FP.One), FP.FromRatio(4, 100));
		var obliqueBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(obliqueBody, BodyType.Dynamic, new FWorldTransform(new FPos(20.ToFP().To64(), 20.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(obliqueBody, obliqueCapsule);
		ref readonly var oblique = ref obliqueBody.Read<Body>();
		Check("an oriented, offset thin capsule gets a floored inertia with bounded inverse diagonal",
			oblique.InvInertiaLocal.Cx.X > FP.Zero && oblique.InvInertiaLocal.Cx.X <= maxInverse
			&& oblique.InvInertiaLocal.Cy.Y > FP.Zero && oblique.InvInertiaLocal.Cy.Y <= maxInverse
			&& oblique.InvInertiaLocal.Cz.Z > FP.Zero && oblique.InvInertiaLocal.Cz.Z <= maxInverse);
		var tinyBody = W.NewEntity<Default>();
		BodyOperations.CreateBody(tinyBody, BodyType.Dynamic, new FWorldTransform(new FPos(10.ToFP().To64(), 20.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		var shapeCountBeforeTiny = W.Query<All<Shape>>().EntitiesCount();
		Check("a dynamic body whose inverse mass exceeds the envelope is rejected at creation", Throws<ArgumentOutOfRangeException>(() =>
			ShapeFactory.CreateShape(tinyBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(3, 100))))
			&& W.Query<All<Shape>>().EntitiesCount() == shapeCountBeforeTiny);
		Check("checked Fixed64 narrowing diagnoses overflow", Throws<OverflowException>(() =>
			_ = Fixed64.FP.FromRatio(32768, 1).To32Checked()));

		var world = W.GetResource<PhysicsWorld>();
		world.SubStepCount = 0;
		Check("invalid solver configuration fails before stepping", Throws<ArgumentOutOfRangeException>(() => Systems.Update()));
		world.SubStepCount = 4;
		Shutdown();
	}

	/// <summary>
	/// A body falling past the escape line must leave the simulation (disabled + tagged) instead of
	/// throwing mid-step or narrowing an out-of-range coordinate; moving it back and re-enabling restores it.
	/// </summary>
	private static void EscapingBodyIsDisabledTest() {
		Console.WriteLine("--- EscapingBodyIsDisabledTest ---");
		Bootstrap();

		var startY = -(PhysicsValidation.EscapeCoordinate - FP.Half);
		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, startY.To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(body, Shape.MakeSphere(FVector3.Zero, FP.Half));
		BodyOperations.SetLinearVelocity(body, new FVector3(FP.Zero, -60.ToFP(), FP.Zero));

		var threw = false;
		try {
			for (var i = 0; i < 5; i++) {
				W.Tick();
				Systems.Update();
			}
		} catch (Exception) {
			threw = true;
		}

		Check("stepping a body across the escape line does not throw", !threw);
		Check("the escaped body is disabled and tagged", !BodyOperations.IsEnabled(body.Read<Body>()) && body.Has<OutOfPhysicsBounds>());
		Check("the escaped body's proxies and contacts are released", PhysicsDiagnostics.Capture().Proxies == 0);

		Check("re-enabling an escaped body in place is rejected", Throws<InvalidOperationException>(() => BodyOperations.Enable(body))
			&& !BodyOperations.IsEnabled(body.Read<Body>()) && body.Has<OutOfPhysicsBounds>());

		BodyOperations.SetTransform(body, FWorldTransform.Identity);
		BodyOperations.Enable(body);
		Check("moving the body back and re-enabling it clears the tag", BodyOperations.IsEnabled(body.Read<Body>()) && !body.Has<OutOfPhysicsBounds>());

		Shutdown();
	}

	/// <summary>Physics configuration is world state: a rollback must restore the snapshot tick's values.</summary>
	private static void PhysicsConfigurationRollbackTest() {
		Console.WriteLine("--- PhysicsConfigurationRollbackTest ---");
		Bootstrap();

		var world = W.GetResource<PhysicsWorld>();
		world.Gravity = new FVector3(FP.Zero, -7.ToFP(), FP.Zero);
		world.ContactHertz = 24.ToFP();
		world.MaximumLinearSpeed = 48.ToFP();
		world.EnableWarmStarting = false;
		world.SubStepCount = 3;
		var snapshot = W.Serializer.CreateWorldSnapshot();

		world.Gravity = new FVector3(FP.Zero, -9.ToFP(), FP.Zero);
		world.ContactHertz = 30.ToFP();
		world.MaximumLinearSpeed = 60.ToFP();
		world.EnableWarmStarting = true;
		world.SubStepCount = 4;
		W.Tick();
		Systems.Update();

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		var restored = W.GetResource<PhysicsWorld>();
		Check("rollback restores the snapshot tick's physics configuration",
			restored.Gravity == new FVector3(FP.Zero, -7.ToFP(), FP.Zero)
			&& restored.ContactHertz == 24.ToFP()
			&& restored.MaximumLinearSpeed == 48.ToFP()
			&& !restored.EnableWarmStarting
			&& restored.SubStepCount == 3);

		Shutdown();
	}

	private static void CrossWorldFullSyncTest() {
		Console.WriteLine("--- CrossWorldFullSyncTest ---");
		Bootstrap();
		var sourceWorld = W.GetResource<PhysicsWorld>();
		sourceWorld.Gravity = new FVector3(FP.Zero, -7.ToFP(), FP.Zero);
		sourceWorld.ContactHertz = 24.ToFP();
		sourceWorld.ContactDampingRatio = 8.ToFP();
		sourceWorld.ContactSpeed = 2.ToFP();
		sourceWorld.RestitutionThreshold = FP.Half;
		sourceWorld.HitEventThreshold = FP.FromRatio(3, 2);
		sourceWorld.MaximumLinearSpeed = 48.ToFP();
		sourceWorld.EnableWarmStarting = false;
		sourceWorld.SubStepCount = 3;

		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, FWorldTransform.Identity);
		ShapeFactory.CreateShape(ground, Shape.MakeBox(FVector3.Zero, new FVector3(4.ToFP(), FP.Half, 4.ToFP())));
		var ball = W.NewEntity<Default>();
		BodyOperations.CreateBody(ball, BodyType.Dynamic,
			new FWorldTransform(new FPos(Fixed64.FP.Zero, 3.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		ShapeFactory.CreateShape(ball, Shape.MakeSphere(FVector3.Zero, FP.Half));
		BodyOperations.SetLinearVelocity(ball, new FVector3(FP.One, FP.Zero, FP.Zero));
		for (var i = 0; i < 5; i++) {
			W.Tick();
			Systems.Update();
		}

		Core<SyncTargetWorld>.W.Create(Core<SyncTargetWorld>.GameWorldSetup.WorldConfig);
		Core<SyncTargetWorld>.Systems.Create(snapshotGuid: Core<SyncTargetWorld>.GameSystemsSnapshotGuid);
		GameTypes.Register<SyncTargetWorld>();
		Core<SyncTargetWorld>.W.SetResource(new Core<SyncTargetWorld>.PhysicsWorld());
		Core<SyncTargetWorld>.W.SetResource(new Core<SyncTargetWorld>.BroadPhase());
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.DamageSystem(), order: 0);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.ProjectileDespawnSystem(), order: 0);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.DeathSystem(), order: 1);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.ShapeProxySystem(), order: 2);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.ContactSystem(), order: 3);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.ContactSolverSystem(), order: 4);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.BodyTransformSyncSystem(), order: 5);
		Core<SyncTargetWorld>.Systems.Add(new Core<SyncTargetWorld>.ProjectileHitSystem(), order: 6);
		Core<SyncTargetWorld>.W.Initialize();
		Core<SyncTargetWorld>.Systems.Initialize();

		var buffer = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		new GameWorldFullSyncHandler().WriteFullSync(ref buffer);
		var reader = buffer.AsReader();
		new Core<SyncTargetWorld>.GameWorldFullSyncHandler().ReadFullSync(ref reader);

		var targetWorld = Core<SyncTargetWorld>.W.GetResource<Core<SyncTargetWorld>.PhysicsWorld>();
		var configMatches = targetWorld.Gravity == sourceWorld.Gravity
			&& targetWorld.ContactHertz == sourceWorld.ContactHertz
			&& targetWorld.ContactDampingRatio == sourceWorld.ContactDampingRatio
			&& targetWorld.ContactSpeed == sourceWorld.ContactSpeed
			&& targetWorld.RestitutionThreshold == sourceWorld.RestitutionThreshold
			&& targetWorld.HitEventThreshold == sourceWorld.HitEventThreshold
			&& targetWorld.MaximumLinearSpeed == sourceWorld.MaximumLinearSpeed
			&& targetWorld.EnableWarmStarting == sourceWorld.EnableWarmStarting
			&& targetWorld.SubStepCount == sourceWorld.SubStepCount;
		Check("full sync restores serialized physics configuration into a distinct world type", configMatches);
		var sourceCounts = PhysicsDiagnostics.Capture();
		var targetCounts = Core<SyncTargetWorld>.PhysicsDiagnostics.Capture();
		Check("full sync restores identical physics counts into a distinct world type",
			sourceCounts.Bodies == targetCounts.Bodies && sourceCounts.Shapes == targetCounts.Shapes
			&& sourceCounts.Proxies == targetCounts.Proxies && sourceCounts.Contacts == targetCounts.Contacts
			&& sourceCounts.CachedPairs == targetCounts.CachedPairs && sourceCounts.MovedProxies == targetCounts.MovedProxies);
		Core<SyncTargetWorld>.W.GetResource<Core<SyncTargetWorld>.BroadPhase>().Validate();

		var synchronizedStateMatches = ball.GID.TryUnpack<SyncTargetWorld>(out var targetBall);
		for (var i = 0; i < 20; i++) {
			W.Tick();
			Systems.Update();
			Core<SyncTargetWorld>.W.Tick();
			Core<SyncTargetWorld>.Systems.Update();
			if (!ball.GID.TryUnpack<SyncTargetWorld>(out targetBall)) {
				synchronizedStateMatches = false;
				continue;
			}
			ref readonly var sourceBody = ref ball.Read<Body>();
			ref readonly var targetBody = ref targetBall.Read<Body>();
			var sourceStepCounts = PhysicsDiagnostics.Capture();
			var targetStepCounts = Core<SyncTargetWorld>.PhysicsDiagnostics.Capture();
			synchronizedStateMatches &= sourceBody.Transform == targetBody.Transform
				&& sourceBody.Center == targetBody.Center
				&& sourceBody.LocalCenter == targetBody.LocalCenter
				&& sourceBody.LinearVelocity == targetBody.LinearVelocity
				&& sourceBody.AngularVelocity == targetBody.AngularVelocity
				&& sourceBody.Mass == targetBody.Mass
				&& sourceBody.Inertia == targetBody.Inertia
				&& sourceStepCounts.Bodies == targetStepCounts.Bodies
				&& sourceStepCounts.Shapes == targetStepCounts.Shapes
				&& sourceStepCounts.Proxies == targetStepCounts.Proxies
				&& sourceStepCounts.Contacts == targetStepCounts.Contacts
				&& sourceStepCounts.CachedPairs == targetStepCounts.CachedPairs;
		}
		Check("distinct synchronized worlds retain identical canonical physics state", synchronizedStateMatches);

		Core<SyncTargetWorld>.Systems.Destroy();
		Core<SyncTargetWorld>.W.Destroy();
		Shutdown();
	}

	/// <summary>FNV-1a 64 over the first <paramref name="length"/> bytes -- a stable, process-independent byte hash (unlike GetHashCode).</summary>
	private static ulong Fnv1a64(byte[] data, int length) {
		const ulong offset = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;

		var hash = offset;
		for (var i = 0; i < length; i++) {
			hash ^= data[i];
			hash *= prime;
		}

		return hash;
	}

	private static (double x, double y, double z, double speed) SampleSphere(W.Entity sphereBody) {
		ref readonly var body = ref sphereBody.Read<Body>();
		return (
			Fixed64.FConversions.ToDouble(body.Transform.Position.X),
			Fixed64.FConversions.ToDouble(body.Transform.Position.Y),
			Fixed64.FConversions.ToDouble(body.Transform.Position.Z),
			FVector3.Length(body.LinearVelocity).ToDouble());
	}

	private static (Fixed64.FP y, FP speed, int contacts) SampleState(W.Entity ballBody) {
		ref readonly var body = ref ballBody.Read<Body>();
		var contacts = W.Query<All<Contact>>().EntitiesCount();
		return (body.Transform.Position.Y, FVector3.Length(body.LinearVelocity), contacts);
	}

	private static void Check(string label, bool condition) {
		if (condition) {
			Console.WriteLine($"PASS: {label}");
		} else {
			Console.WriteLine($"FAIL: {label}");
			_failures++;
		}
	}

	private static bool NearlyEqual(FP a, FP b) => FP.Abs(a - b) <= FP.FromRatio(1, 1000);

	private static bool Throws<TException>(Action action) where TException : Exception {
		try {
			action();
			return false;
		} catch (TException) {
			return true;
		}
	}
}
