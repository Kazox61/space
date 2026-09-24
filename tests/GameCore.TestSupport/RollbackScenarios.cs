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
		_systemsCreated = true;
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
		_systemsCreated = false;
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
		_syncTargetSystemsCreated = true;
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
		_syncTargetSystemsCreated = false;
		Core<SyncTargetWorld>.W.Destroy();
		Shutdown();
	}

	private static (Fixed64.FP y, FP speed, int contacts) SampleState(W.Entity ballBody) {
		ref readonly var body = ref ballBody.Read<Body>();
		var contacts = W.Query<All<Contact>>().EntitiesCount();
		return (body.Transform.Position.Y, FVector3.Length(body.LinearVelocity), contacts);
	}
}
