using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Shenanicode.Rollback;
using Space.GameCore;
using Fixed32;
using Fixed;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

public struct TestWorld : IWorldType, ISessionType { }

/// <summary>Mirrors GameWorldPrev/WP: a second parallel world the client deserializes into every simulated tick for render interpolation (GameInterpolationReceiver.SaveInterpolationState).</summary>
public struct TestWorldPrev : IWorldType { }

public abstract class WPrev : World<TestWorldPrev> { }

public static class Program {
	private static int _failures;
	private const int TickRate = 60;

	public static void Main(string[] args) {
		if (args.Length > 0 && args[0] == "visual") {
			VisualTest.Run();
			return;
		}

		if (args.Length > 0 && args[0] == "bench") {
			BenchResimulationCost();
			BenchInterpolationSnapshotCost();
			return;
		}

		TouchEventsTest();
		DropAndRestTest();
		CapsuleOnSphereSmokeTest();
		BoxOnSphereSmokeTest();
		BoxOnBoxSmokeTest();
		BoxOnCapsuleSmokeTest();
		OverlappingSpawnStressTest();
		RollbackRoundTripTest();
		KinematicBodyDrivenByVelocityPushesDynamicBodyTest();
		MoverPushesRestingSphereStablyTest();
		MoverBlockedByStaticWallTest();
		MoverStressTestAgainstRealGroundSize();
		RollingResistanceBringsPushedSphereToRestTest();
		MoverFallsLandsAndJumpsTest();
		SensorDoesNotProduceCollisionResponseTest();
		RayCastHitsSphereCapsuleAndBoxTest();
		RayCastMissAndFilterTest();
		PogoGroundingRayTest();

		if (_failures == 0) {
			Console.WriteLine("ALL CHECKS PASSED");
		}
		else {
			Console.WriteLine($"{_failures} CHECK(S) FAILED");
			Environment.Exit(1);
		}
	}

	internal static void Bootstrap() {
		W.Create(GameWorldSetup.WorldConfig);
		Systems.Create();
		W.Types().RegisterAll(typeof(CoreRoot).Assembly);
		W.SetResource(new PhysicsWorld());
		W.SetResource(new BroadPhase());
		Systems.Add(new ShapeProxySystem(), order: 0);
		Systems.Add(new ContactSystem(), order: 1);
		Systems.Add(new ContactSolverSystem(), order: 2);
		Systems.Add(new BodyTransformSyncSystem(), order: 3);
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
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(5, 1)));

		var fallingBody = W.NewEntity<Default>();
		fallingBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(7, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(fallingBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio(3, 1)));

		W.Tick();
		Systems.Update();

		Check("ContactBeginTouchEvent fires when overlapping", beginReceiver.ReadAll(static _ => { }) == 1);
		Check("ContactEndTouchEvent does not fire yet", endReceiver.ReadAll(static _ => { }) == 0);

		ref var body = ref fallingBody.Mut<Body>();
		body.Transform.Position = new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(500, 1), Fixed64.FP.Zero);

		W.Tick();
		Systems.Update();

		Check("ContactEndTouchEvent fires after separation", endReceiver.ReadAll(static _ => { }) == 1);

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

			if (tick == 0) firstY = y;
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
		}
		catch (Exception e) {
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
		}
		else if (grounded) {
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
		}
		catch (Exception e) {
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

		var beginReceiver = W.RegisterEventReceiver<ContactBeginTouchEvent>();

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
		ShapeFactory.CreateShape(sensorBody, sensorShape);

		var sphereBody = W.NewEntity<Default>();
		sphereBody.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		var sphereShape = Shape.MakeSphere(FVector3.Zero, FP.One);
		sphereShape.Density = FP.One;
		ShapeFactory.CreateShape(sphereBody, sphereShape);

		var sawSensorTouch = false;
		var minY = 10.0;

		for (var tick = 0; tick < 300; tick++) {
			W.Tick();
			Systems.Update();

			if (beginReceiver.ReadAll(static _ => { }) > 0) {
				sawSensorTouch = true;
			}

			var y = Fixed64.FConversions.ToDouble(sphereBody.Read<Body>().Transform.Position.Y);
			minY = Math.Min(minY, y);
		}

		var finalY = Fixed64.FConversions.ToDouble(sphereBody.Read<Body>().Transform.Position.Y);

		Check("touch events still fire for the sensor overlap", sawSensorTouch);
		Check("sphere falls straight through the sensor instead of resting on it (rests at ~1.5, not ~6.5)", Math.Abs(finalY - 1.5) < 0.05);
		Check("sphere never got hung up on the sensor on the way down (dipped below its bottom face at 4.5)", minY < 4.5);

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

	/// <summary>Verifies the negative cases the 5-way contract depends on: a genuine miss, a sensor being skipped, and Filter excluding a shape.</summary>
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

		var origin = new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero);
		var translation = new FVector3(FP.Zero, -FP.FromRatio(20, 1), FP.Zero);
		Check("ray skips a sensor shape", !PhysicsQueries.CastRayClosest(broadPhase, origin, translation, Filter.Default, out _));

		var filteredBody = W.NewEntity<Default>();
		filteredBody.Set(new Body { Type = BodyType.Static, Transform = new FWorldTransform(new FPos(Fixed64.FP.FromRatio(100, 1), Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity) });
		var filteredShape = Shape.MakeSphere(FVector3.Zero, FP.FromRatio(2, 1));
		filteredShape.Filter = new Filter { CategoryBits = 2, MaskBits = ulong.MaxValue, GroupIndex = 0 };
		ShapeFactory.CreateShape(filteredBody, filteredShape);

		var queryFilter = new Filter { CategoryBits = ulong.MaxValue, MaskBits = 1, GroupIndex = 0 };
		var filteredOrigin = new FPos(Fixed64.FP.FromRatio(100, 1), Fixed64.FP.FromRatio(10, 1), Fixed64.FP.Zero);
		Check("ray respects Filter.ShouldCollide", !PhysicsQueries.CastRayClosest(broadPhase, filteredOrigin, translation, queryFilter, out _));

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
		ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

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
		}
		else {
			Console.WriteLine($"FAIL: {label}");
			_failures++;
		}
	}
}
