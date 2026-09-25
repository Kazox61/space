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

	private static void ShapeCastFarBelowLargeBoxTest() {
		Console.WriteLine("--- ShapeCastFarBelowLargeBoxTest ---");
		// The sample level's 80x1x80 ground and the mover's ground-probe box, 150 units below it: the
		// first GJK vertex is ~180 away, past where Q16.16 squared lengths wrap.
		var ground = Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP()));
		var quarter = FP.FromRatio(1, 4);
		var probe = new ShapeProxy { Count = 8, Radius = FP.Zero };
		for (var i = 0; i < 8; i++) {
			probe.Points[i] = new FVector3((i & 1) != 0 ? quarter : -quarter, (i & 2) != 0 ? quarter : -quarter, (i & 4) != 0 ? quarter : -quarter);
		}

		var threw = false;
		var hit = false;
		try {
			var output = Distance.ShapeCast(new ShapeCastPairInput {
				ProxyA = ground.MakeProxy(),
				ProxyB = probe,
				Transform = new FTransform(new FVector3(-16.ToFP(), -150.ToFP(), -100.ToFP()), FQuaternion.Identity),
				TranslationB = new FVector3(FP.Zero, -FP.One, FP.Zero),
				MaxFraction = FP.One,
				CanEncroach = true,
			});
			hit = output.Hit;
		} catch (ArgumentOutOfRangeException) {
			threw = true;
		}

		Check("a shape cast far from a large box does not overflow", !threw);
		Check("a shape cast far from a large box misses it", !threw && !hit);
	}

	private static void FarLargeRadiusDistanceTest() {
		Console.WriteLine("--- FarLargeRadiusDistanceTest ---");
		// A validation-legal static capsule whose radius (20) exceeds FarDistance, and a sphere 40 out
		// from its segment's middle. The core bounding-sphere gap (20) alone would take the far path
		// and, minus the radii, report -0.5; the true distance is 19.5.
		var capsule = Shape.MakeCapsule(new FVector3(FP.Zero, -20.ToFP(), FP.Zero), new FVector3(FP.Zero, 20.ToFP(), FP.Zero), 20.ToFP());
		var sphere = Shape.MakeSphere(FVector3.Zero, FP.Half);
		var identity = new FWorldTransform(FPos.Zero, FQuaternion.Identity);
		Check("the large-radius capsule is a legal static shape", !Throws<ArgumentOutOfRangeException>(() =>
			PhysicsValidation.ValidateShape(capsule, BodyType.Static, identity, nameof(capsule))));

		var cache = SimplexCache.Empty;
		var output = Distance.ShapeDistance(new DistanceInput {
			ProxyA = capsule.MakeProxy(),
			ProxyB = sphere.MakeProxy(),
			Transform = new FTransform(new FVector3(40.ToFP(), FP.Zero, FP.Zero), FQuaternion.Identity),
			UseRadii = true,
		}, ref cache);

		Check("a sphere far from a large-radius capsule does not overlap it", output.Distance >= B3Config.OverlapSlop);
		Check("a sphere far from a large-radius capsule reports its true distance",
			FP.Abs(output.Distance - FP.FromRatio(195, 10)) < FP.FromRatio(1, 100));
	}

	private static void FarDistanceWorstCasePairTest() {
		Console.WriteLine("--- FarDistanceWorstCasePairTest ---");
		// The largest static and dynamic boxes validation allows, diagonal to each other with a gap just
		// under FarDistance (16), so GJK runs with its longest possible simplex vectors. If either extent
		// grows past what Distance.FarDistance's remarks budget for, Q16.16 wraps and this goes wrong.
		var staticExtent = PhysicsValidation.MaximumStaticExtent;
		var dynamicExtent = PhysicsValidation.MaximumDynamicExtent;
		var large = Shape.MakeBox(FVector3.Zero, new FVector3(staticExtent, staticExtent, staticExtent));
		var small = Shape.MakeBox(FVector3.Zero, new FVector3(dynamicExtent, dynamicExtent, dynamicExtent));
		var identity = new FWorldTransform(FPos.Zero, FQuaternion.Identity);
		Check("the largest static box is legal", !Throws<ArgumentOutOfRangeException>(() =>
			PhysicsValidation.ValidateShape(large, BodyType.Static, identity, nameof(large))));
		Check("the largest dynamic box is legal", !Throws<ArgumentOutOfRangeException>(() =>
			PhysicsValidation.ValidateShape(small, BodyType.Dynamic, identity, nameof(small))));

		var expected = FP.FromRatio(155, 10);
		var offset = staticExtent + dynamicExtent + expected / FP.Sqrt(3.ToFP());
		var cache = SimplexCache.Empty;
		var output = Distance.ShapeDistance(new DistanceInput {
			ProxyA = large.MakeProxy(),
			ProxyB = small.MakeProxy(),
			Transform = new FTransform(new FVector3(offset, offset, offset), FQuaternion.Identity),
			UseRadii = true,
		}, ref cache);

		Check("the worst-case pair inside FarDistance reports its true distance",
			FP.Abs(output.Distance - expected) < FP.FromRatio(5, 100));
	}

	private static void CapsuleAtParallelBoxEdgeManifoldTest() {
		Console.WriteLine("--- CapsuleAtParallelBoxEdgeManifoldTest ---");
		var box = Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Half, FP.Half));
		var capsule = Shape.MakeCapsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		// Upright capsule beside the box's vertical edge at (-0.5, y, -0.5): the core segment is 0.4879
		// from that edge, so the capsule overlaps it by ~0.012 and SAT alone finds no separating axis.
		var capsuleXf = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(-845, 1000), Fixed64.FP.FromRatio(1, 2), Fixed64.FP.FromRatio(-845, 1000)), FQuaternion.Identity);
		var boxXf = new FWorldTransform(FPos.Zero, FQuaternion.Identity);

		var capsuleFirst = Manifold.Collide(capsule, capsuleXf, box, boxXf);
		Check("upright capsule touching a parallel box edge produces a contact", capsuleFirst.PointCount > 0);
		Check("the edge contact is shallow", capsuleFirst.PointCount > 0 && capsuleFirst.MinSeparation() < FP.Zero && capsuleFirst.MinSeparation() > -FP.FromRatio(3, 100));
		Check("the edge contact normal points diagonally from capsule to box",
			capsuleFirst.Normal.X > FP.FromRatio(6, 10) && capsuleFirst.Normal.Z > FP.FromRatio(6, 10));

		var boxFirst = Manifold.Collide(box, boxXf, capsule, capsuleXf);
		Check("box-first order produces the same edge contact", boxFirst.PointCount > 0 && boxFirst.Normal.X < -FP.FromRatio(6, 10));
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
