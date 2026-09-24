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

}
