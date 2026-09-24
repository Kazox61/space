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

}
