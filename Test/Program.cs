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
		OverlappingSpawnStressTest();
		RollbackRoundTripTest();

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
