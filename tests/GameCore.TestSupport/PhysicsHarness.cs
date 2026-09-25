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

public sealed record PhysicsScenario(string Name, string Category, Action Execute);

public static partial class Program {
	private static int _failures;
	private static bool _systemsCreated;
	private static bool _syncTargetSystemsCreated;
	private const int TickRate = 60;

	public static IReadOnlyList<PhysicsScenario> Scenarios { get; } = BuildScenarios();

	public static void RunScenario(PhysicsScenario scenario) {
		CleanupResidualState();
		_failures = 0;
		try {
			scenario.Execute();
			if (_failures != 0) {
				throw new InvalidOperationException($"{scenario.Name} failed {_failures} check(s).");
			}
		} finally {
			CleanupResidualState();
		}
	}

	public static bool RunBenchmarks(bool enforce) {
		if (!enforce) {
			BenchResimulationCost();
			BenchInterpolationSnapshotCost();
		}

		return BenchPhysicsBudgets();
	}

	private static IReadOnlyList<PhysicsScenario> BuildScenarios() => new PhysicsScenario[] {
		new(nameof(TouchEventsTest), "Contacts", TouchEventsTest),
		new(nameof(ContactPersistenceTest), "Contacts", ContactPersistenceTest),
		new(nameof(ParallelCapsuleManifoldTest), "Collision", ParallelCapsuleManifoldTest),
		new(nameof(CapsuleAtParallelBoxEdgeManifoldTest), "Collision", CapsuleAtParallelBoxEdgeManifoldTest),
		new(nameof(ShapeCastFarBelowLargeBoxTest), "Collision", ShapeCastFarBelowLargeBoxTest),
		new(nameof(FarLargeRadiusDistanceTest), "Collision", FarLargeRadiusDistanceTest),
		new(nameof(FarDistanceWorstCasePairTest), "Collision", FarDistanceWorstCasePairTest),
		new(nameof(QuerySpanValidationTest), "Validation", QuerySpanValidationTest),
		new(nameof(FeatureIdValidityTest), "Collision", FeatureIdValidityTest),
		new(nameof(ParallelCapsuleRestingStabilityTest), "Collision", ParallelCapsuleRestingStabilityTest),
		new(nameof(SpeculativeContactAndEventFlagsTest), "Contacts", SpeculativeContactAndEventFlagsTest),
		new(nameof(ContactHitEventTest), "Contacts", ContactHitEventTest),
		new(nameof(DropAndRestTest), "Simulation", DropAndRestTest),
		new(nameof(FrictionIsStableAcrossSubstepsTest), "Simulation", FrictionIsStableAcrossSubstepsTest),
		new(nameof(CapsuleOnSphereSmokeTest), "Simulation", CapsuleOnSphereSmokeTest),
		new(nameof(BoxOnSphereSmokeTest), "Simulation", BoxOnSphereSmokeTest),
		new(nameof(BoxOnBoxSmokeTest), "Simulation", BoxOnBoxSmokeTest),
		new(nameof(BoxOnCapsuleSmokeTest), "Simulation", BoxOnCapsuleSmokeTest),
		new(nameof(OverlappingSpawnStressTest), "Simulation", OverlappingSpawnStressTest),
		new(nameof(RollbackRoundTripTest), "Rollback", RollbackRoundTripTest),
		new(nameof(RollbackStateHashReplayTest), "Rollback", RollbackStateHashReplayTest),
		new(nameof(KinematicBodyDrivenByVelocityPushesDynamicBodyTest), "CharacterMover", KinematicBodyDrivenByVelocityPushesDynamicBodyTest),
		new(nameof(MoverPushesRestingSphereStablyTest), "CharacterMover", MoverPushesRestingSphereStablyTest),
		new(nameof(MoverBlockedByStaticWallTest), "CharacterMover", MoverBlockedByStaticWallTest),
		new(nameof(MoverStressTestAgainstRealGroundSize), "CharacterMover", MoverStressTestAgainstRealGroundSize),
		new(nameof(RollingResistanceBringsPushedSphereToRestTest), "CharacterMover", RollingResistanceBringsPushedSphereToRestTest),
		new(nameof(MoverFallsLandsAndJumpsTest), "CharacterMover", MoverFallsLandsAndJumpsTest),
		new(nameof(SensorDoesNotProduceCollisionResponseTest), "Sensors", SensorDoesNotProduceCollisionResponseTest),
		new(nameof(SensorSensorDirectionalEventsTest), "Sensors", SensorSensorDirectionalEventsTest),
		new(nameof(RayCastHitsSphereCapsuleAndBoxTest), "Queries", RayCastHitsSphereCapsuleAndBoxTest),
		new(nameof(RayCastMissAndFilterTest), "Queries", RayCastMissAndFilterTest),
		new(nameof(HollowSphereRayFractionTest), "Queries", HollowSphereRayFractionTest),
		new(nameof(WorldShapeQueriesTest), "Queries", WorldShapeQueriesTest),
		new(nameof(WorldQueryCallbackContractTest), "Queries", WorldQueryCallbackContractTest),
		new(nameof(CharacterMoverFilterTest), "CharacterMover", CharacterMoverFilterTest),
		new(nameof(MoverVelocityIgnoresInactivePlanesTest), "CharacterMover", MoverVelocityIgnoresInactivePlanesTest),
		new(nameof(MoverPushesCrateByItsCornerTest), "CharacterMover", MoverPushesCrateByItsCornerTest),
		new(nameof(RotatingTimeOfImpactTest), "ContinuousCollision", RotatingTimeOfImpactTest),
		new(nameof(AutomaticFastBodyCcdTest), "ContinuousCollision", AutomaticFastBodyCcdTest),
		new(nameof(AngularCcdTest), "ContinuousCollision", AngularCcdTest),
		new(nameof(BulletCcdTargetPolicyTest), "ContinuousCollision", BulletCcdTargetPolicyTest),
		new(nameof(MaximumSpeedBulletCcdTest), "ContinuousCollision", MaximumSpeedBulletCcdTest),
		new(nameof(BulletCcdRollbackTest), "Rollback", BulletCcdRollbackTest),
		new(nameof(PogoGroundingRayTest), "CharacterMover", PogoGroundingRayTest),
		new(nameof(KinematicProjectileKillsDummyTest), "Gameplay", KinematicProjectileKillsDummyTest),
		new(nameof(RejectedBroadPhasePairsTest), "Collision", RejectedBroadPhasePairsTest),
		new(nameof(BodyTransformMutationTest), "Mutation", BodyTransformMutationTest),
		new(nameof(StaticTransformMutationTest), "Mutation", StaticTransformMutationTest),
		new(nameof(BodyTypeAndEnabledMutationTest), "Mutation", BodyTypeAndEnabledMutationTest),
		new(nameof(ShapeFilterMutationTest), "Mutation", ShapeFilterMutationTest),
		new(nameof(ShapeGeometryDensityAndForcesTest), "Mutation", ShapeGeometryDensityAndForcesTest),
		new(nameof(MutationRollbackTest), "Rollback", MutationRollbackTest),
		new(nameof(ProjectileDespawnTtlTest), "Lifecycle", ProjectileDespawnTtlTest),
		new(nameof(SessionInputDuplicateRetryTest), "Rollback", SessionInputDuplicateRetryTest),
		new(nameof(BodyDestructionRollbackTest), "Rollback", BodyDestructionRollbackTest),
		new(nameof(DeathSystemPhysicsLifecycleTest), "Lifecycle", DeathSystemPhysicsLifecycleTest),
		new(nameof(ProjectileLifecycleCountsTest), "Lifecycle", ProjectileLifecycleCountsTest),
		new(nameof(FixedPointBoundaryValidationTest), "Validation", FixedPointBoundaryValidationTest),
		new(nameof(EscapingBodyIsDisabledTest), "Validation", EscapingBodyIsDisabledTest),
		new(nameof(PhysicsConfigurationRollbackTest), "Rollback", PhysicsConfigurationRollbackTest),
		new(nameof(CrossWorldFullSyncTest), "Rollback", CrossWorldFullSyncTest),
		new(nameof(SleepingStackFallsAsleepTest), "Sleeping", SleepingStackFallsAsleepTest),
		new(nameof(WakeOnImpulsePropagatesThroughIslandTest), "Sleeping", WakeOnImpulsePropagatesThroughIslandTest),
		new(nameof(IndependentIslandsSleepIndependentlyTest), "Sleeping", IndependentIslandsSleepIndependentlyTest),
		new(nameof(DestroyingSupportWakesIslandTest), "Sleeping", DestroyingSupportWakesIslandTest),
		new(nameof(MovingKinematicKeepsIslandAwakeTest), "Sleeping", MovingKinematicKeepsIslandAwakeTest),
		new(nameof(MoverPushWakesSleepingBodyTest), "Sleeping", MoverPushWakesSleepingBodyTest),
		new(nameof(MoverPushesSleepingCrateTest), "Sleeping", MoverPushesSleepingCrateTest),
		new(nameof(WorldSleepToggleTest), "Sleeping", WorldSleepToggleTest),
		new(nameof(SleepRollbackReplayTest), "Rollback", SleepRollbackReplayTest),
		new(nameof(DiagnosticsDetectStaleStateTest), "Diagnostics", DiagnosticsDetectStaleStateTest),
		new(nameof(BroadPhaseHealthTest), "Diagnostics", BroadPhaseHealthTest),
		new(nameof(SteadyStateAllocationTest), "Performance", SteadyStateAllocationTest),
		new(nameof(NestedQueryReentrancyTest), "Queries", NestedQueryReentrancyTest),
		new(nameof(RemoteAttackNotRepeatedTest), "Fx", RemoteAttackNotRepeatedTest),
		new(nameof(PredictedJumpNotRepeatedTest), "Fx", PredictedJumpNotRepeatedTest),
		new(nameof(FxDedupAcrossRollbackTest), "Fx", FxDedupAcrossRollbackTest),
		new(nameof(FxMispredictNotRetractedTest), "Fx", FxMispredictNotRetractedTest),
		new(nameof(FxHitKeyedByProjectileTest), "Fx", FxHitKeyedByProjectileTest),
		new(nameof(FxLateGuardTest), "Fx", FxLateGuardTest),
		new(nameof(FxPruningTest), "Fx", FxPruningTest),
		new(nameof(FxFullSyncClearsLogTest), "Fx", FxFullSyncClearsLogTest),
		new(nameof(FxTickAfterHardResetTest), "Fx", FxTickAfterHardResetTest),
		new(nameof(ProjectileOriginSurvivesRollbackTest), "Fx", ProjectileOriginSurvivesRollbackTest),
		new(nameof(PrevWorldTracksEntitiesOneTickBehindTest), "Interpolation", PrevWorldTracksEntitiesOneTickBehindTest),
		new(nameof(TeleportStampTest), "Interpolation", TeleportStampTest),
		new(nameof(CorrectionProbeMeasuresMispredictionTest), "Interpolation", CorrectionProbeMeasuresMispredictionTest),
		new(nameof(CorrectionProbeFullSyncTest), "Interpolation", CorrectionProbeFullSyncTest),
		new(nameof(CorrectionSmootherTest), "Interpolation", CorrectionSmootherTest),
	};

	internal static void Bootstrap() {
		W.Create(GameWorldSetup.WorldConfig);
		Systems.Create(snapshotGuid: GameSystemsSnapshotGuid);
		_systemsCreated = true;
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
		_systemsCreated = false;
		W.Destroy();
	}

	private static void CleanupResidualState() {
		FxSink = null;
		RollbackObserver = null;
		DestroyPrevWorld();
		if (_syncTargetSystemsCreated) {
			Core<SyncTargetWorld>.Systems.Destroy();
			_syncTargetSystemsCreated = false;
		}
		if (Core<SyncTargetWorld>.W.Status != WorldStatus.NotCreated) {
			Core<SyncTargetWorld>.W.Destroy();
		}
		if (_systemsCreated) {
			Systems.Destroy();
			_systemsCreated = false;
		}
		if (W.Status != WorldStatus.NotCreated) {
			W.Destroy();
		}
		if (S.Status != SessionStatus.NotCreated) {
			S.Destroy();
		}
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
	private static void Check(string label, bool condition) {
		if (condition) {
			Console.WriteLine($"PASS: {label}");
		} else {
			Console.WriteLine($"FAIL: {label}");
			_failures++;
		}
	}
}
