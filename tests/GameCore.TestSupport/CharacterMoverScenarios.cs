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

	private static (double x, double y, double z, double speed) SampleSphere(W.Entity sphereBody) {
		ref readonly var body = ref sphereBody.Read<Body>();
		return (
			Fixed64.FConversions.ToDouble(body.Transform.Position.X),
			Fixed64.FConversions.ToDouble(body.Transform.Position.Y),
			Fixed64.FConversions.ToDouble(body.Transform.Position.Z),
			FVector3.Length(body.LinearVelocity).ToDouble());
	}
}
