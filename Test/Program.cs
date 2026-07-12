using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Space.GameCore;
using Fixed32;
using Fixed;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

public struct TestWorld : IWorldType, ISessionType { }

public static class Program {
	private static int _failures;
	private const int TickRate = 60;

	public static void Main(string[] args) {
		if (args.Length > 0 && args[0] == "visual") {
			VisualTest.Run();
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
		RollingResistanceBringsPushedSphereToRestTest();
		MoverFallsLandsAndJumpsTest();

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
	/// gravity, ground-plane-derived grounding, and jump (jump checked before the grounded reset so
	/// it isn't immediately stomped back to zero, matching box3d's CharacterMover::Step ordering).
	/// </summary>
	private static void StepMoverWithGravity(BroadPhase broadPhase, ref FWorldTransform moverXf, ref FVector3 moverVelocity, ref bool grounded, Capsule capsule, FVector3 horizontalVelocity, bool jumpPressed, FVector3 gravity, FP jumpForce, FP dt) {
		if (jumpPressed && grounded) {
			moverVelocity.Y = jumpForce;
			grounded = false;
		}
		else if (grounded) {
			moverVelocity.Y = FP.Zero;
		}

		moverVelocity = new FVector3(horizontalVelocity.X, moverVelocity.Y + gravity.Y * dt, horizontalVelocity.Z);

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
		grounded = MoverSolver.IsGrounded(in planes);
	}

	/// <summary>
	/// Regression test for gravity/ground-detection/jump (PlayerMoverSystem): a mover dropped above
	/// the ground falls, lands, and is reported grounded; jumping while grounded launches it upward
	/// and immediately clears grounded; it then falls back and lands again.
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
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(5, 1), Fixed64.FP.Zero), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var grounded = false;
		var gravity = new FVector3(FP.Zero, -FP.FromRatio(10, 1), FP.Zero);
		var jumpForce = FP.FromRatio(12, 1);
		var dt = Space.GameCore.Const.DeltaTime.To32();

		for (var tick = 0; tick < 90; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();

		}

		var yAfterFalling = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		Console.WriteLine($"  after falling: y={yAfterFalling:F4} grounded={grounded}");
		Check("mover lands on the ground and settles near rest height (0.5)", Math.Abs(yAfterFalling - 0.5) < 0.05);
		Check("mover is reported grounded after landing", grounded);

		// Jump for exactly one tick (edge-triggered, like Godot's IsActionJustPressed).
		StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, capsule, FVector3.Zero, true, gravity, jumpForce, dt);
		W.Tick();
		Systems.Update();

		Check("jump clears grounded immediately", !grounded);

		// Jump force 12 over gravity 10 takes ~1.2s (72 ticks) just to reach the peak -- give the
		// ascent loop enough budget to actually capture it, not cut it off mid-flight.
		var maxYAfterJump = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		for (var tick = 0; tick < 80; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();
			maxYAfterJump = Math.Max(maxYAfterJump, Fixed64.FConversions.ToDouble(moverXf.Position.Y));
		}

		Console.WriteLine($"  peak height after jump: {maxYAfterJump:F4}");
		Check("jump actually launches the mover upward (well above the 0.5 rest height)", maxYAfterJump > 1.0);

		// Let it fall back down and land again -- descent from the peak takes about as long as the
		// ascent did, plus margin to actually settle.
		for (var tick = 0; tick < 200; tick++) {
			StepMoverWithGravity(broadPhase, ref moverXf, ref moverVelocity, ref grounded, capsule, FVector3.Zero, false, gravity, jumpForce, dt);
			W.Tick();
			Systems.Update();
		}

		var yAfterLandingAgain = Fixed64.FConversions.ToDouble(moverXf.Position.Y);
		Console.WriteLine($"  after falling back down: y={yAfterLandingAgain:F4} grounded={grounded}");
		Check("mover lands again after the jump", Math.Abs(yAfterLandingAgain - 0.5) < 0.05);
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
