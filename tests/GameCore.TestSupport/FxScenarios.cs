using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

/// <summary>Rollback-safe one-shot effects: <see cref="FxLog"/> and the systems that report to it. See docs/rollback-presentation-events.md §6.</summary>
public static partial class Program {
	private const ushort LocalChannel = 0;
	private const ushort RemoteChannel = 1;

	private sealed class RecordingFxPlayer : IFxPlayer {
		public readonly List<(FxEvent Fx, int Age)> Played = new();

		public void Play(in FxEvent fx, int ageTicks) => Played.Add((fx, ageTicks));

		public int Count(FxKind kind) => Played.FindAll(p => p.Fx.Kind == kind).Count;
	}

	/// <summary>Forwards to an <see cref="FxLog"/> and counts raw reports, to prove a tick really was re-simulated.</summary>
	private sealed class CountingFxSink : IFxSink {
		public readonly FxLog Log;
		public readonly List<(FxEvent Fx, int Tick)> Recorded = new();

		public CountingFxSink(FxLog log) => Log = log;

		public void Record(in FxEvent fx, int tick) {
			Recorded.Add((fx, tick));
			Log.Record(fx, tick);
		}

		public void Clear() => Log.Clear();

		public int Count(FxKind kind) => Recorded.FindAll(r => r.Fx.Kind == kind).Count;
	}

	/// <summary>
	/// The full game simulation on a session, the way the client runs it: players on
	/// <see cref="LocalChannel"/> and <see cref="RemoteChannel"/>, a first saved frame (the client
	/// saves one right after its full sync), and a recording FX sink.
	/// </summary>
	private static CountingFxSink StartFxSession(SimulationType simulationType, Action? buildScene = null) {
		S.Create(simulationType, GameSessionSetup.SessionConfig);
		S.Types().Signal<PlayerConnectedSignal>().Signal<PlayerDisconnectedSignal>();
		GameSessionSetup.Register();
		S.Initialize();
		GameWorldSetup.CreateAndInitialize(TestLevels.Arena);
		_systemsCreated = true;

		W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = LocalChannel });
		W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = RemoteChannel });
		buildScene?.Invoke();
		S.SaveFrame();

		var sink = new CountingFxSink(new FxLog(S.RollbackTicksCapacity) { LocalChannel = LocalChannel });
		FxSink = sink;
		return sink;
	}

	private static PlayerInput AttackRight => new() { AttackX = Fixed64.FP.One };

	private static void Advance(int ticks) => S.FastForwardToTick(S.CurrentTick + ticks);

	/// <summary>Advances one tick at a time and flushes after each, like <c>ClientGame._Process</c>.</summary>
	private static void AdvanceAndFlush(CountingFxSink sink, IFxPlayer player, int ticks) {
		for (var i = 0; i < ticks; i++) {
			Advance(1);
			sink.Log.Flush(S.CurrentTick, player);
		}
	}

	/// <summary>
	/// §1.1: a predicted tick carries the last input forward, so reading the attack with
	/// <c>LastFresh</c> queued a ghost shot on every predicted tick after a remote attack arrived.
	/// </summary>
	private static void RemoteAttackNotRepeatedTest() {
		Console.WriteLine("--- RemoteAttackNotRepeatedTest ---");
		var sink = StartFxSession(SimulationType.ForwardOnly);
		// Long enough that no queued shot is released during the test.
		Systems.GetResource<CharacterRes>().AttackDelay = Fixed64.FP.One;

		Advance(3);
		S.SetApprovedInput(RemoteChannel, AttackRight);
		Advance(6);

		Check("a remote attack followed by predicted ticks queues one shot", W.Query<All<PendingShot>>().EntitiesCount() == 1);
		Check("a remote attack followed by predicted ticks reports one AttackStarted", sink.Count(FxKind.AttackStarted) == 1);
	}

	/// <summary>A held jump carried forward on predicted ticks must not jump again after landing.</summary>
	private static void PredictedJumpNotRepeatedTest() {
		Console.WriteLine("--- PredictedJumpNotRepeatedTest ---");
		StartFxSession(SimulationType.ForwardOnly);

		// Let the players settle onto the ground first.
		Advance(60);
		S.SetApprovedInput(RemoteChannel, new PlayerInput { Jump = true });
		Advance(1);
		var approvedJumpStarted = false;
		foreach (var player in W.Query<All<PlayerInfo, Mover>>().Entities()) {
			if (player.Read<PlayerInfo>().InputChannel == RemoteChannel) {
				ref readonly var mover = ref player.Read<Mover>();
				approvedJumpStarted = !mover.Grounded && mover.Velocity.Y > FP.Zero;
				break;
			}
		}
		Check("the approved remote jump starts airborne with upward velocity", approvedJumpStarted);

		// The approved jump itself is airborne now; count only jumps that start from the ground again.
		var jumps = 0;
		var wasGrounded = false;
		for (var i = 0; i < 120; i++) {
			Advance(1);
			foreach (var player in W.Query<All<PlayerInfo, Mover>>().Entities()) {
				if (player.Read<PlayerInfo>().InputChannel != RemoteChannel) {
					continue;
				}
				var grounded = player.Read<Mover>().Grounded;
				if (wasGrounded && !grounded && player.Read<Mover>().Velocity.Y > FP.Zero) {
					jumps++;
				}
				wasGrounded = grounded;
			}
		}

		Check("one jump press carried forward by prediction does not jump again after landing", jumps == 0);
	}

	/// <summary>A rollback over the attack tick with identical input re-reports the attack, and it plays once.</summary>
	private static void FxDedupAcrossRollbackTest() {
		Console.WriteLine("--- FxDedupAcrossRollbackTest ---");
		var sink = StartFxSession(SimulationType.AutomaticRollbacks);
		var player = new RecordingFxPlayer();

		Advance(22);
		S.SetPredictionInput(LocalChannel, AttackRight);
		Advance(8);
		sink.Log.Flush(S.CurrentTick, player);

		// A correction to another channel before the attack forces a re-simulation over it.
		S.SetApprovedInputAt(21, RemoteChannel, new PlayerInput { MoveX = Fixed64.FP.One });
		Advance(1);
		sink.Log.Flush(S.CurrentTick, player);

		Check("the rollback re-simulated the attack tick", sink.Count(FxKind.AttackStarted) == 2);
		Check("the re-simulated attack is played once", player.Count(FxKind.AttackStarted) == 1);
		Check("the played attack carries its input tick", player.Played.Count > 0 && player.Played[0].Fx.KeyTick == 22);
	}

	/// <summary>A predicted attack that a correction removes has already played and nothing retracts it.</summary>
	private static void FxMispredictNotRetractedTest() {
		Console.WriteLine("--- FxMispredictNotRetractedTest ---");
		var sink = StartFxSession(SimulationType.AutomaticRollbacks);
		var player = new RecordingFxPlayer();
		Systems.GetResource<CharacterRes>().AttackDelay = Fixed64.FP.One;

		Advance(22);
		S.SetPredictionInput(LocalChannel, AttackRight);
		Advance(3);
		sink.Log.Flush(S.CurrentTick, player);
		Check("the predicted attack plays", player.Count(FxKind.AttackStarted) == 1);

		S.SetApprovedInputAt(22, LocalChannel, new PlayerInput());
		Advance(1);
		sink.Log.Flush(S.CurrentTick, player);

		Check("the correction removed the attack from the simulation", W.Query<All<PendingShot>>().EntitiesCount() == 0);
		Check("the corrected-away attack still counts as played once", player.Count(FxKind.AttackStarted) == 1);
	}

	/// <summary>
	/// An arrow hits a wall; a rollback over its spawn re-creates the arrow entity, and the hit is
	/// reported again (possibly on another tick). The hit is keyed by shooter and spawn tick, so it plays once.
	/// </summary>
	private static void FxHitKeyedByProjectileTest() {
		Console.WriteLine("--- FxHitKeyedByProjectileTest ---");
		// A wall between the local player (x = 0) and the remote one (x = 3).
		var sink = StartFxSession(SimulationType.AutomaticRollbacks, static () => {
			var wall = W.NewEntity<Default>();
			var wallTransform = new FWorldTransform(new FPos(Fixed64.FP.Two, Fixed64.FP.One + Fixed64.FP.Half, Fixed64.FP.Zero), FQuaternion.Identity);
			BodyOperations.CreateBody(wall, BodyType.Static, wallTransform);
			ShapeFactory.CreateShape(wall, Shape.MakeBox(FVector3.Zero, new FVector3(FP.Half, FP.Two, FP.Two)));
		});
		var player = new RecordingFxPlayer();
		Systems.GetResource<CharacterRes>().AttackDelay = Space.GameCore.Const.DeltaTime;

		Advance(22);
		S.SetPredictionInput(LocalChannel, AttackRight);
		AdvanceAndFlush(sink, player, 20);
		Check("the arrow hit the wall", sink.Count(FxKind.ProjectileHit) >= 1);

		S.SetApprovedInputAt(21, RemoteChannel, new PlayerInput { MoveX = Fixed64.FP.One });
		Advance(1);
		sink.Log.Flush(S.CurrentTick, player);

		Check("the rollback re-simulated the hit", sink.Count(FxKind.ProjectileHit) >= 2);
		Check("the re-simulated hit is played once", player.Count(FxKind.ProjectileHit) == 1);
		Check("the hit is keyed by the shooter's channel", player.Played.Exists(p => p.Fx.Kind == FxKind.ProjectileHit && p.Fx.Channel == LocalChannel));

		// The arrow flies +x into the wall's face at x = 1.5, so the impact faces back out along -x.
		var hit = player.Played.Find(p => p.Fx.Kind == FxKind.ProjectileHit).Fx;
		Check("the hit is placed on the wall's face", Math.Abs(Fixed64.FConversions.ToDouble(hit.Position.X) - 1.5) < 0.05);
		Check("the hit faces out of the wall", Fixed64.FConversions.ToDouble(hit.Direction.X) < 0.0
			&& Math.Abs(Fixed64.FConversions.ToDouble(hit.Direction.Y)) < 0.05 && Math.Abs(Fixed64.FConversions.ToDouble(hit.Direction.Z)) < 0.05);

		// The same hit reported a tick later, somewhere else, is still the same hit.
		var shifted = hit;
		shifted.Position += Fixed64.FVector3.Up;
		sink.Log.Record(shifted, S.CurrentTick - 1);
		sink.Log.Flush(S.CurrentTick, player);
		Check("a hit shifted by a tick and moved is not played again", player.Count(FxKind.ProjectileHit) == 1);
	}

	/// <summary>
	/// A rollback over a projectile's spawn re-creates it, and a hard-reset snapshot load bumps the
	/// version of entity slots in segments that were empty when saved -- so the arrow can come back
	/// under a new GID. <c>EntityViewUpdater</c> therefore keys projectile views by
	/// <see cref="ProjectileOrigin"/>, which must come back unchanged and unique.
	/// </summary>
	private static void ProjectileOriginSurvivesRollbackTest() {
		Console.WriteLine("--- ProjectileOriginSurvivesRollbackTest ---");
		StartFxSession(SimulationType.AutomaticRollbacks);
		Systems.GetResource<CharacterRes>().AttackDelay = Space.GameCore.Const.DeltaTime;

		Advance(22);
		S.SetPredictionInput(LocalChannel, AttackRight);
		Advance(8);
		var before = ProjectileOrigins();

		S.SetApprovedInputAt(21, RemoteChannel, new PlayerInput { MoveX = Fixed64.FP.One });
		Advance(1);
		var after = ProjectileOrigins();

		Check("one projectile before the rollback", before.Count == 1);
		Check("the same single projectile origin after the rollback", after.Count == 1 && after[0].Origin.Equals(before[0].Origin));
		Console.WriteLine($"GID before {before[0].Gid}, after {(after.Count > 0 ? after[0].Gid.ToString() : "-")}");

		static List<(EntityGID Gid, ProjectileOrigin Origin)> ProjectileOrigins() {
			var origins = new List<(EntityGID, ProjectileOrigin)>();
			foreach (var projectile in W.Query<All<ProjectileOrigin>>().Entities()) {
				origins.Add((projectile.GID, projectile.Read<ProjectileOrigin>()));
			}
			return origins;
		}
	}

	/// <summary>Local cosmetic effects are skipped when late; remote attacks play late with their age.</summary>
	private static void FxLateGuardTest() {
		Console.WriteLine("--- FxLateGuardTest ---");
		var log = new FxLog(rollbackTicks: 125) { LocalChannel = LocalChannel };
		var player = new RecordingFxPlayer();

		log.Record(new FxEvent { Kind = FxKind.AttackStarted, Channel = LocalChannel, KeyTick = 100 }, 100);
		log.Record(new FxEvent { Kind = FxKind.ProjectileHit, Channel = RemoteChannel, KeyTick = 90 }, 100);
		log.Record(new FxEvent { Kind = FxKind.AttackStarted, Channel = RemoteChannel, KeyTick = 100 }, 100);
		log.Flush(headTick: 121, player);

		Check("a late local attack is skipped", !player.Played.Exists(p => p.Fx.Channel == LocalChannel));
		Check("a late cosmetic hit is skipped", player.Count(FxKind.ProjectileHit) == 0);
		Check("a late remote attack still plays", player.Count(FxKind.AttackStarted) == 1 && player.Played[0].Fx.Channel == RemoteChannel);
		Check("a late remote attack carries its age", player.Played.Count == 1 && player.Played[0].Age == 20);

		log.Record(new FxEvent { Kind = FxKind.ProjectileHit, Channel = LocalChannel, KeyTick = 110 }, 120);
		log.Flush(headTick: 121, player);
		Check("an on-time hit plays with age 0", player.Count(FxKind.ProjectileHit) == 1 && player.Played[^1].Age == 0);
		Check("every late guard is shorter than the prune window",
			log.LateTicks(new FxEvent { Kind = FxKind.AttackStarted, Channel = RemoteChannel }) < log.PruneWindow);
	}

	/// <summary>Keys are forgotten once no rollback can reach their tick.</summary>
	private static void FxPruningTest() {
		Console.WriteLine("--- FxPruningTest ---");
		var log = new FxLog(rollbackTicks: 125);
		var player = new RecordingFxPlayer();

		log.Record(new FxEvent { Kind = FxKind.ProjectileHit, KeyTick = 0 }, 10);
		log.Flush(headTick: 11, player);
		log.Flush(headTick: 10 + log.PruneWindow, player);
		Check("a key inside the rollback window is kept", log.SeenCount == 1);

		// A re-simulation reporting the key again moves its tick forward, so it stays kept.
		log.Record(new FxEvent { Kind = FxKind.ProjectileHit, KeyTick = 0 }, 50);
		log.Flush(headTick: 11 + log.PruneWindow, player);
		Check("a key re-reported later is kept by its latest tick", log.SeenCount == 1);

		log.Flush(headTick: 51 + log.PruneWindow, player);
		Check("a key older than the rollback window is pruned", log.SeenCount == 0);
		Check("re-reporting never replayed the key", player.Played.Count == 1);
	}

	/// <summary>A full sync clears the log through the handler, and new events still dispatch.</summary>
	private static void FxFullSyncClearsLogTest() {
		Console.WriteLine("--- FxFullSyncClearsLogTest ---");
		Bootstrap();
		var log = new FxLog(rollbackTicks: 125);
		var player = new RecordingFxPlayer();
		FxSink = log;

		var attack = new FxEvent { Kind = FxKind.AttackStarted, KeyTick = 5 };
		log.Record(attack, 5);
		log.Flush(headTick: 6, player);

		var buffer = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		var handler = new GameWorldFullSyncHandler();
		handler.WriteFullSync(ref buffer);
		var reader = buffer.AsReader();
		handler.ReadFullSync(ref reader);
		Check("a full sync empties the log", log.SeenCount == 0);

		log.Record(attack, 5);
		log.Flush(headTick: 6, player);
		Check("events on the new timeline still dispatch", player.Played.Count == 2);
	}

	/// <summary>The first tick simulated after a hard reset reports its own tick.</summary>
	private static void FxTickAfterHardResetTest() {
		Console.WriteLine("--- FxTickAfterHardResetTest ---");
		var sink = StartFxSession(SimulationType.ForwardOnly);

		S.HardReset(100);
		S.SetPredictionInput(LocalChannel, AttackRight);
		Advance(1);

		Check("the first event after a hard reset carries the start tick",
			sink.Recorded.Count == 1 && sink.Recorded[0].Tick == 100 && sink.Recorded[0].Fx.KeyTick == 100);
	}
}
