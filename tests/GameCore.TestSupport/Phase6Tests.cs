using System;
using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

/// <summary>Phase 6 (scale and observability): sleeping, islands, allocations, diagnostics, budgets.</summary>
public static partial class Program {
	/// <summary>
	/// Allocation budget for a steady-state tick of <see cref="BuildLoadScene"/> (physics systems plus
	/// character movers), averaged over the sample window. Structural churn (spawning, contact
	/// creation) is excluded by measuring a scene whose contact set is stable.
	/// </summary>
	private const long SteadyTickAllocationBudgetBytes = 0;

	// Release-build timing budgets for the load scene; see docs/physics-budgets.md.
	private const double RegularTickBudgetMs = 1.0;
	private const double TypicalRollbackBudgetMs = 8.0;
	private const double FullRollbackBudgetMs = 33.3;
	private const int TypicalRollbackTicks = 30;
	// Session default rollback capacity: (framesCapacity 26 - 1) * saveEachNthTick 5.
	private const int FullRollbackTicks = 125;

	internal static void RunPhase6Tests() {
		SleepingStackFallsAsleepTest();
		WakeOnImpulsePropagatesThroughIslandTest();
		IndependentIslandsSleepIndependentlyTest();
		DestroyingSupportWakesIslandTest();
		MovingKinematicKeepsIslandAwakeTest();
		MoverPushWakesSleepingBodyTest();
		WorldSleepToggleTest();
		SleepRollbackReplayTest();
		DiagnosticsDetectStaleStateTest();
		BroadPhaseHealthTest();
		SteadyStateAllocationTest();
		NestedQueryReentrancyTest();
	}

	private static FPos Pos(int x, int y, int z, int denominator = 1) =>
		new(Fixed64.FP.FromRatio(x, denominator), Fixed64.FP.FromRatio(y, denominator), Fixed64.FP.FromRatio(z, denominator));

	private static W.Entity CreateStaticGround(int halfExtent = 20) {
		var ground = W.NewEntity<Default>();
		BodyOperations.CreateBody(ground, BodyType.Static, new FWorldTransform(FPos.Zero, FQuaternion.Identity));
		ShapeFactory.CreateShape(ground, Shape.MakeBox(FVector3.Zero, new FVector3(halfExtent.ToFP(), FP.Half, halfExtent.ToFP())));
		return ground;
	}

	private static W.Entity CreateDynamicBox(FPos position, FP halfExtent) {
		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Dynamic, new FWorldTransform(position, FQuaternion.Identity));
		ShapeFactory.CreateShape(body, Shape.MakeBox(FVector3.Zero, new FVector3(halfExtent, halfExtent, halfExtent)));
		return body;
	}

	private static W.Entity CreateDynamicSphere(FPos position, FP radius) {
		var body = W.NewEntity<Default>();
		BodyOperations.CreateBody(body, BodyType.Dynamic, new FWorldTransform(position, FQuaternion.Identity));
		ShapeFactory.CreateShape(body, Shape.MakeSphere(FVector3.Zero, radius));
		return body;
	}

	/// <summary>Three stacked unit boxes resting on the ground at x = <paramref name="x"/>.</summary>
	private static W.Entity[] CreateStack(int x, int height = 3) {
		var boxes = new W.Entity[height];
		for (var i = 0; i < height; i++) {
			boxes[i] = CreateDynamicBox(Pos(x, 2 * i + 2, 0, 2), FP.Half);
		}
		return boxes;
	}

	private static void Step(int ticks) {
		for (var tick = 0; tick < ticks; tick++) {
			W.Tick();
			Systems.Update();
		}
	}

	private static bool AllSleeping(W.Entity[] bodies) {
		foreach (var body in bodies) {
			if (!BodyOperations.IsSleeping(body)) {
				return false;
			}
		}
		return true;
	}

	private static bool AllAwake(W.Entity[] bodies) {
		foreach (var body in bodies) {
			if (BodyOperations.IsSleeping(body)) {
				return false;
			}
		}
		return true;
	}

	private static void SleepingStackFallsAsleepTest() {
		Console.WriteLine("--- SleepingStackFallsAsleepTest ---");
		Bootstrap();
		CreateStaticGround();
		var stack = CreateStack(0);

		Step(20);
		Check("a freshly settling stack is awake", AllAwake(stack) && PhysicsDiagnostics.LastStep.AwakeBodies == 3);

		Step(300);
		var stats = PhysicsDiagnostics.LastStep;
		Check($"a resting stack falls asleep as one island ({stats})", AllSleeping(stack));
		Check("a sleeping scene simulates no bodies, constraints, or narrowphase pairs",
			stats.AwakeBodies == 0 && stats.SleepingBodies == 3 && stats.Constraints == 0 && stats.ContactsUpdated == 0 && stats.ContactsSleeping > 0);
		Check("sleeping bodies carry no velocity", stack[2].Read<Body>().LinearVelocity == FVector3.Zero && stack[2].Read<Body>().AngularVelocity == FVector3.Zero);

		var top = stack[2].Read<Body>().Transform;
		var contacts = PhysicsDiagnostics.Capture().Contacts;
		Step(120);
		Check("sleeping bodies do not move and keep their contacts",
			stack[2].Read<Body>().Transform.Position == top.Position && PhysicsDiagnostics.Capture().Contacts == contacts);
		Check("the stack rests at its expected height", Math.Abs(Fixed64.FConversions.ToDouble(top.Position.Y) - 3.0) < 0.05);
		PhysicsDiagnostics.Validate();
		Shutdown();
	}

	private static void WakeOnImpulsePropagatesThroughIslandTest() {
		Console.WriteLine("--- WakeOnImpulsePropagatesThroughIslandTest ---");
		Bootstrap();
		CreateStaticGround();
		var stack = CreateStack(0);
		Step(320);
		Check("stack is asleep before the impulse", AllSleeping(stack));

		BodyOperations.ApplyLinearImpulseToCenter(stack[0], new FVector3(FP.FromRatio(1, 10), FP.Zero, FP.Zero));
		Check("the impulse wakes the touched body immediately", !BodyOperations.IsSleeping(stack[0]) && BodyOperations.IsSleeping(stack[2]));
		Step(1);
		Check("the whole island wakes before the next solve", AllAwake(stack) && PhysicsDiagnostics.LastStep.BodiesWoken >= 3);

		Step(400);
		Check("the disturbed stack settles back to sleep", AllSleeping(stack));
		PhysicsDiagnostics.Validate();
		Shutdown();
	}

	private static void IndependentIslandsSleepIndependentlyTest() {
		Console.WriteLine("--- IndependentIslandsSleepIndependentlyTest ---");
		Bootstrap();
		CreateStaticGround();
		var left = CreateStack(-4);
		var right = CreateStack(4);
		Step(20);
		Check("two stacks sharing only static ground form two islands", PhysicsDiagnostics.LastStep.Islands == 2);
		Step(300);
		Check("both stacks fall asleep", AllSleeping(left) && AllSleeping(right));

		BodyOperations.Wake(left[1]);
		Step(1);
		Check("waking one stack leaves the other asleep (static bodies do not link islands)", AllAwake(left) && AllSleeping(right));
		Shutdown();
	}

	private static void DestroyingSupportWakesIslandTest() {
		Console.WriteLine("--- DestroyingSupportWakesIslandTest ---");
		Bootstrap();
		CreateStaticGround();
		var stack = CreateStack(0);
		Step(320);
		Check("stack is asleep before losing its support", AllSleeping(stack));
		var restingHeight = stack[2].Read<Body>().Transform.Position.Y;

		BodyOperations.DestroyBody(stack[0]);
		Check("destroying a supporting body wakes the bodies it touched", !BodyOperations.IsSleeping(stack[1]));
		Step(60);
		Check("the remaining boxes fall instead of hanging in the air", stack[2].Read<Body>().Transform.Position.Y < restingHeight - Fixed64.FP.Half);
		PhysicsDiagnostics.Validate();
		Shutdown();
	}

	private static void MovingKinematicKeepsIslandAwakeTest() {
		Console.WriteLine("--- MovingKinematicKeepsIslandAwakeTest ---");
		Bootstrap();
		CreateStaticGround();
		var platform = W.NewEntity<Default>();
		BodyOperations.CreateBody(platform, BodyType.Kinematic, new FWorldTransform(Pos(0, 1, 0), FQuaternion.Identity));
		ShapeFactory.CreateShape(platform, Shape.MakeBox(FVector3.Zero, new FVector3(3.ToFP(), FP.FromRatio(1, 4), 3.ToFP())));
		BodyOperations.SetLinearVelocity(platform, new FVector3(FP.FromRatio(1, 2), FP.Zero, FP.Zero));
		var rider = CreateDynamicBox(Pos(0, 7, 0, 4), FP.Half);

		Step(300);
		Check("a body riding a moving kinematic platform never sleeps", !BodyOperations.IsSleeping(rider) && !BodyOperations.IsSleeping(platform));
		Check("the rider is carried by the platform", rider.Read<Body>().Transform.Position.X > Fixed64.FP.One);

		BodyOperations.SetLinearVelocity(platform, FVector3.Zero);
		Step(120);
		Check("once the platform stops, platform and rider sleep together", BodyOperations.IsSleeping(rider) && BodyOperations.IsSleeping(platform));

		BodyOperations.SetLinearVelocity(platform, new FVector3(FP.FromRatio(1, 2), FP.Zero, FP.Zero));
		Step(1);
		Check("restarting the platform wakes the rider through the island", !BodyOperations.IsSleeping(rider));
		Shutdown();
	}

	private static void MoverPushWakesSleepingBodyTest() {
		Console.WriteLine("--- MoverPushWakesSleepingBodyTest ---");
		Bootstrap();
		CreateStaticGround();
		var ball = CreateDynamicSphere(Pos(3, 1, 0), FP.Half);
		Step(240);
		Check("resting ball is asleep", BodyOperations.IsSleeping(ball));

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(Pos(1, 1, 0, 2), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var dt = Space.GameCore.Const.DeltaTime.To32();
		var startX = ball.Read<Body>().Transform.Position.X;
		for (var tick = 0; tick < 90; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, new FVector3(3.ToFP(), FP.Zero, FP.Zero), dt);
			Step(1);
		}
		Check("the character mover's push wakes and moves a sleeping ball", ball.Read<Body>().Transform.Position.X > startX + Fixed64.FP.Half);
		Shutdown();
	}

	private static void MoverPushesSleepingCrateTest() {
		Console.WriteLine("--- MoverPushesSleepingCrateTest ---");
		Bootstrap();
		CreateStaticGround();

		var obstacle = W.NewEntity<Default>();
		BodyOperations.CreateBody(obstacle, BodyType.Static, new FWorldTransform(Pos(5, 1, 0), FQuaternion.Identity));
		ShapeFactory.CreateShape(obstacle, Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.Half, 2.ToFP())));

		// The crate rests against the side of the same 4x1x4 static box used by the sample level.
		var crate = CreateDynamicBox(Pos(5, 2, 0, 2), FP.Half);
		Step(240);
		Check("production-sized crate resting against a static box is asleep", BodyOperations.IsSleeping(crate));

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(Pos(5, 3, -2, 2), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var dt = Space.GameCore.Const.DeltaTime.To32();
		var startZ = crate.Read<Body>().Transform.Position.Z;

		for (var tick = 0; tick < 90; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, new FVector3(FP.Zero, FP.Zero, 3.ToFP()), dt);
			Step(1);
		}

		Check("the character mover wakes a sleeping crate against a static box", !BodyOperations.IsSleeping(crate));
		Check("the character mover slides a sleeping crate along a static box", crate.Read<Body>().Transform.Position.Z > startZ + Fixed64.FP.Half);
		Shutdown();
	}

	private static void MoverPushesCrateByItsCornerTest() {
		Console.WriteLine("--- MoverPushesCrateByItsCornerTest ---");
		Bootstrap();
		CreateStaticGround();
		var crate = CreateDynamicBox(Pos(0, 1, 0), FP.Half);
		Step(180);

		var broadPhase = W.GetResource<BroadPhase>();
		var capsule = new Capsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half);
		var moverXf = new FWorldTransform(Pos(-3, 3, -3, 2), FQuaternion.Identity);
		var moverVelocity = FVector3.Zero;
		var velocity = 7 * FVector3.Normalize(new FVector3(FP.One, FP.Zero, FP.One));
		var dt = Space.GameCore.Const.DeltaTime.To32();

		// Walking diagonally straight into the crate's vertical edge.
		for (var tick = 0; tick < 40; tick++) {
			StepMover(broadPhase, ref moverXf, ref moverVelocity, capsule, velocity, dt);
			Step(1);
		}

		var cratePosition = crate.Read<Body>().Transform.Position;
		Check("the character mover pushes a crate it walks into corner-first", cratePosition.X > Fixed64.FP.One && cratePosition.Z > Fixed64.FP.One);
		Check("the crate does not stop the mover at its corner", moverXf.Position.X > Fixed64.FP.Zero);
		Shutdown();
	}

	private static void WorldSleepToggleTest() {
		Console.WriteLine("--- WorldSleepToggleTest ---");
		Bootstrap();
		CreateStaticGround();
		var stack = CreateStack(0);
		Step(320);
		Check("stack sleeps with world sleeping enabled", AllSleeping(stack));

		W.GetResource<PhysicsWorld>().EnableSleep = false;
		Step(1);
		Check("disabling world sleeping wakes every body", AllAwake(stack));
		Step(300);
		Check("with world sleeping disabled nothing falls asleep", AllAwake(stack) && stack[0].Read<Body>().SleepTime == FP.Zero);

		W.GetResource<PhysicsWorld>().EnableSleep = true;
		BodyOperations.SetSleepEnabled(stack[1], false);
		Step(300);
		Check("one body with sleep disabled keeps its whole island awake", AllAwake(stack));
		Shutdown();
	}

	/// <summary>Sleep state lives in Body, so rollback must reproduce falling asleep and waking exactly.</summary>
	private static void SleepRollbackReplayTest() {
		Console.WriteLine("--- SleepRollbackReplayTest ---");
		Bootstrap();
		CreateStaticGround();
		var stack = CreateStack(0);
		var hashWriter = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		ulong HashState() {
			hashWriter.Position = 0;
			W.Serializer.CreateWorldSnapshot(ref hashWriter);
			return Fnv1a64(hashWriter.Buffer, (int)hashWriter.Position);
		}

		Step(40);
		var snapshot = W.Serializer.CreateWorldSnapshot();
		const int ticks = 360;
		const int wakeTick = 300;
		var live = new ulong[ticks];
		for (var tick = 0; tick < ticks; tick++) {
			if (tick == wakeTick) {
				BodyOperations.ApplyLinearImpulseToCenter(stack[2], new FVector3(FP.FromRatio(1, 5), FP.Zero, FP.Zero));
			}
			Step(1);
			live[tick] = HashState();
		}
		var sleptBeforeWake = true;

		W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		var diverged = -1;
		for (var tick = 0; tick < ticks; tick++) {
			if (tick == wakeTick) {
				sleptBeforeWake = AllSleeping(stack);
				BodyOperations.ApplyLinearImpulseToCenter(stack[2], new FVector3(FP.FromRatio(1, 5), FP.Zero, FP.Zero));
			}
			Step(1);
			if (diverged < 0 && HashState() != live[tick]) {
				diverged = tick;
			}
		}
		Console.WriteLine($"STATE-HASH sleep-replay 0x{live[ticks - 1]:x16}");
		Check("the replay falls asleep before the wake impulse", sleptBeforeWake);
		Check($"rollback replay through sleep and wake reproduces every state hash{(diverged >= 0 ? $" (diverged at {diverged})" : "")}", diverged < 0);
		Shutdown();
	}

	private static void DiagnosticsDetectStaleStateTest() {
		Console.WriteLine("--- DiagnosticsDetectStaleStateTest ---");
		Bootstrap();
		CreateStaticGround();
		// A box, not a sphere: a sphere rolls indefinitely without rolling resistance.
		var ball = CreateDynamicBox(Pos(0, 1, 0), FP.Half);
		Step(240);
		Check("resting box is asleep", BodyOperations.IsSleeping(ball));
		PhysicsDiagnostics.Validate();

		ball.Ref<Body>().LinearVelocity = new FVector3(FP.One, FP.Zero, FP.Zero);
		Check("validation flags velocity written directly onto a sleeping body", Throws<InvalidOperationException>(PhysicsDiagnostics.Validate));
		BodyOperations.SetLinearVelocity(ball, new FVector3(FP.One, FP.Zero, FP.Zero));
		PhysicsDiagnostics.Validate();

		Step(240);
		Check("the pushed box slides to rest and sleeps again", BodyOperations.IsSleeping(ball));
		ball.Ref<Body>().Transform.Position += new FVector3(5.ToFP(), FP.Zero, FP.Zero);
		ball.Ref<Body>().Center += new FVector3(5.ToFP(), FP.Zero, FP.Zero);
		Check("validation flags a sleeping body moved without updating its proxy", Throws<InvalidOperationException>(PhysicsDiagnostics.Validate));
		Shutdown();
	}

	private static void BroadPhaseHealthTest() {
		Console.WriteLine("--- BroadPhaseHealthTest ---");
		Bootstrap();
		BuildLoadScene();
		Step(120);
		var trees = PhysicsDiagnostics.CaptureBroadPhase();
		Console.WriteLine($"  {trees.Static}");
		Console.WriteLine($"  {trees.Kinematic}");
		Console.WriteLine($"  {trees.Dynamic}");
		Check("load-scene trees report their proxies and stay balanced",
			trees.Dynamic.Proxies > 0 && trees.Static.Proxies > 0 && !trees.Static.IsDegraded && !trees.Kinematic.IsDegraded && !trees.Dynamic.IsDegraded);
		var stats = PhysicsDiagnostics.LastStep;
		Console.WriteLine($"  {stats}");
		Check("step statistics report phase work and timings",
			stats.AwakeBodies > 0 && stats.ContactsUpdated > 0 && stats.Constraints > 0 && stats.TotalMilliseconds > 0.0
			&& stats.Milliseconds(PhysicsPhase.Solve) > 0.0 && stats.Milliseconds(PhysicsPhase.Narrowphase) > 0.0);
		Shutdown();
	}

	private static void NestedQueryReentrancyTest() {
		Console.WriteLine("--- NestedQueryReentrancyTest ---");
		Bootstrap();
		CreateStaticGround();
		CreateDynamicSphere(Pos(0, 1, 0), FP.Half);
		CreateDynamicSphere(Pos(3, 1, 0), FP.Half);
		Step(1);
		var broadPhase = W.GetResource<BroadPhase>();
		var outer = 0;
		var inner = 0;
		var probe = ShapeProxy.MakePoint(FVector3.Zero, 10.ToFP());
		PhysicsQueries.OverlapShape(broadPhase, new FWorldTransform(Pos(0, 1, 0), FQuaternion.Identity), probe, Filter.Default, QuerySensorMode.Exclude, _ => {
			outer++;
			PhysicsQueries.OverlapShape(broadPhase, new FWorldTransform(Pos(0, 1, 0), FQuaternion.Identity), probe, Filter.Default, QuerySensorMode.Exclude, _ => {
				inner++;
				return true;
			});
			return true;
		});
		Check("a query issued from a query callback sees its own complete candidate set", outer == 3 && inner == 9);
		Shutdown();
	}

	/// <summary>
	/// A production-shaped load: ground, static obstacles, kinematic patrollers, resting piles that
	/// fall asleep, and loose dynamic bodies that keep moving.
	/// </summary>
	private static (W.Entity[] Kinematics, W.Entity[] Dynamics) BuildLoadScene() {
		CreateStaticGround(40);
		for (var i = 0; i < 10; i++) {
			var obstacle = W.NewEntity<Default>();
			BodyOperations.CreateBody(obstacle, BodyType.Static, new FWorldTransform(Pos(-30 + 6 * i, 1, 20), FQuaternion.Identity));
			ShapeFactory.CreateShape(obstacle, Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.Half, 2.ToFP())));
		}

		var kinematics = new W.Entity[10];
		for (var i = 0; i < kinematics.Length; i++) {
			var dummy = W.NewEntity<Default>();
			BodyOperations.CreateBody(dummy, BodyType.Kinematic, new FWorldTransform(Pos(-30 + 6 * i, 2, -20), FQuaternion.Identity));
			ShapeFactory.CreateShape(dummy, Shape.MakeCapsule(new FVector3(FP.Zero, -FP.Half, FP.Zero), new FVector3(FP.Zero, FP.Half, FP.Zero), FP.Half));
			BodyOperations.SetLinearVelocity(dummy, new FVector3(FP.Zero, FP.Zero, (i % 2 == 0 ? 1 : -1).ToFP()));
			kinematics[i] = dummy;
		}

		var dynamics = new W.Entity[60];
		var index = 0;
		for (var pile = 0; pile < 8; pile++) {
			foreach (var box in CreateStack(-28 + 8 * pile, 4)) {
				dynamics[index++] = box;
			}
		}
		for (var i = 0; index < dynamics.Length; i++) {
			var ball = CreateDynamicSphere(Pos(-27 + 2 * i, 3, 8), FP.Half);
			BodyOperations.SetLinearVelocity(ball, new FVector3(FP.Zero, FP.Zero, FP.One));
			dynamics[index++] = ball;
		}
		return (kinematics, dynamics);
	}

	private static void StepLoadMovers(BroadPhase broadPhase, FWorldTransform[] movers, int tick) {
		var capsule = new Capsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
		var dt = Space.GameCore.Const.DeltaTime.To32();
		for (var i = 0; i < movers.Length; i++) {
			var velocity = FVector3.Zero;
			var direction = ((tick / 120 + i) % 2 == 0 ? 2 : -2).ToFP();
			StepMover(broadPhase, ref movers[i], ref velocity, capsule, new FVector3(direction, FP.Zero, FP.Zero), dt);
		}
	}

	private static FWorldTransform[] CreateLoadMovers() {
		var movers = new FWorldTransform[4];
		for (var i = 0; i < movers.Length; i++) {
			movers[i] = new FWorldTransform(Pos(-6 + 4 * i, 1, 0), FQuaternion.Identity);
		}
		return movers;
	}

	private static void SteadyStateAllocationTest() {
		Console.WriteLine("--- SteadyStateAllocationTest ---");
		Bootstrap();
		BuildLoadScene();
		var broadPhase = W.GetResource<BroadPhase>();
		var movers = CreateLoadMovers();
		// Warm up past settling and first-use buffer growth.
		for (var tick = 0; tick < 600; tick++) {
			StepLoadMovers(broadPhase, movers, tick);
			Step(1);
		}

		const int sampleTicks = 300;
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var tick = 0; tick < sampleTicks; tick++) {
			StepLoadMovers(broadPhase, movers, 600 + tick);
			Step(1);
		}
		var total = GC.GetAllocatedBytesForCurrentThread() - before;
		var average = (double)total / sampleTicks;
		var budget = SteadyTickAllocationBudgetBytes * sampleTicks;
		Console.WriteLine($"  steady-state managed allocation: {total} bytes total, {average:F2} bytes/tick ({PhysicsDiagnostics.LastStep})");
		Check($"steady-state ticks allocate at most {budget} bytes total ({SteadyTickAllocationBudgetBytes} bytes/tick; measured {total} total, {average:F2}/tick)", total <= budget);
		Shutdown();
	}

	/// <summary>
	/// Load-scene timing against the documented budgets. Only meaningful in Release on an idle machine,
	/// so it runs in "bench" mode; "bench --enforce" turns budget misses into a failing exit code.
	/// </summary>
	internal static bool BenchPhysicsBudgets() {
		Console.WriteLine("--- BenchPhysicsBudgets ---");
		Bootstrap();
		BuildLoadScene();
		var broadPhase = W.GetResource<BroadPhase>();
		var movers = CreateLoadMovers();
		for (var tick = 0; tick < 600; tick++) {
			StepLoadMovers(broadPhase, movers, tick);
			Step(1);
		}
		Console.WriteLine($"  scene: {PhysicsDiagnostics.Capture()} ({PhysicsDiagnostics.LastStep})");

		const int sampleTicks = 600;
		var worst = 0.0;
		var sw = Stopwatch.StartNew();
		for (var tick = 0; tick < sampleTicks; tick++) {
			var tickStart = sw.Elapsed.TotalMilliseconds;
			StepLoadMovers(broadPhase, movers, 600 + tick);
			Step(1);
			worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - tickStart);
		}
		var average = sw.Elapsed.TotalMilliseconds / sampleTicks;
		var last = PhysicsDiagnostics.LastStep;
		var phases = "";
		for (var phase = PhysicsPhase.ProxyUpdate; phase < PhysicsPhase.Count; phase++) {
			phases += $" {phase}={last.Milliseconds(phase):F3}";
		}
		Console.WriteLine($"  last step phases (ms):{phases}");

		var snapshot = W.Serializer.CreateWorldSnapshot();
		var moverSnapshot = (FWorldTransform[])movers.Clone();
		double Burst(int ticks) {
			var best = double.MaxValue;
			// Best of several runs: the budget concerns the work, not scheduler noise.
			for (var run = 0; run < 5; run++) {
				Array.Copy(moverSnapshot, movers, movers.Length);
				var burst = Stopwatch.StartNew();
				W.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
				for (var tick = 0; tick < ticks; tick++) {
					StepLoadMovers(broadPhase, movers, 1200 + tick);
					Step(1);
				}
				best = Math.Min(best, burst.Elapsed.TotalMilliseconds);
			}
			return best;
		}
		var typical = Burst(TypicalRollbackTicks);
		const int fullTicks = FullRollbackTicks;
		var full = Burst(fullTicks);

		var ok = true;
		void Report(string label, double value, double budget) {
			var pass = value <= budget;
			ok &= pass;
			Console.WriteLine($"  {(pass ? "within" : "OVER ")} {label}: {value:F3} ms (budget {budget:F1} ms)");
		}
		Report("regular tick (average)", average, RegularTickBudgetMs);
		Console.WriteLine($"         regular tick (worst):  {worst:F3} ms");
		Report($"typical rollback burst ({TypicalRollbackTicks} ticks)", typical, TypicalRollbackBudgetMs);
		Report($"full rollback burst ({fullTicks} ticks)", full, FullRollbackBudgetMs);
		Shutdown();
		return ok;
	}
}
