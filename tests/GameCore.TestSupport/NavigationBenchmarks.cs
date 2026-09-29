using System;
using System.Diagnostics;
using System.IO;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

/// <summary>Navigation cost on the committed sample level; see docs/navigation-assessment.md, Phase 6.</summary>
public static partial class Program {
	private const int NavBenchCharacters = 16;

	// Release-build budgets for NavBenchCharacters characters that all re-plan every tick,
	// on top of the same session without navigation.
	private const double NavRegularTickBudgetMs = 1.0;
	private const double NavTypicalRollbackBudgetMs = 8.0;
	private const double NavFullRollbackBudgetMs = 33.3;
	private const int NavFullRollbackTicks = FullRollbackTicks - 10; // The input ring rejects the oldest tick at full capacity.

	/// <summary>
	/// Times regular ticks and rollback bursts of <see cref="TypicalRollbackTicks"/> and
	/// <see cref="NavFullRollbackTicks"/> ticks, forced by a late remote input, with and without the
	/// navmesh. Returns whether the navigation share stays within its budgets.
	/// </summary>
	internal static bool BenchNavigationBudgets() {
		Console.WriteLine("--- BenchNavigationBudgets ---");
		var level = FindSampleLevel();
		var baseline = MeasureNavigationScene(level.WithNavigation(null));
		var navigation = MeasureNavigationScene(level);

		var ok = true;
		void Report(string label, double withNav, double withoutNav, double budget) {
			var cost = withNav - withoutNav;
			var pass = cost <= budget;
			ok &= pass;
			Console.WriteLine($"  {(pass ? "within" : "OVER ")} {label}: {withNav:F3} ms with navigation, {withoutNav:F3} ms without, navigation {cost:F3} ms (budget {budget:F1} ms)");
		}
		Console.WriteLine($"  {NavBenchCharacters} characters re-planning every tick; {navigation.Plans} plans per regular tick on average");
		Report("regular tick (average)", navigation.Tick, baseline.Tick, NavRegularTickBudgetMs);
		Report($"typical rollback burst ({TypicalRollbackTicks} ticks)", navigation.TypicalBurst, baseline.TypicalBurst, NavTypicalRollbackBudgetMs);
		Report($"full rollback burst ({NavFullRollbackTicks} ticks)", navigation.FullBurst, baseline.FullBurst, NavFullRollbackBudgetMs);
		return ok;
	}

	private readonly record struct NavigationSceneTiming(double Tick, double TypicalBurst, double FullBurst, double Plans);

	private static NavigationSceneTiming MeasureNavigationScene(LevelData level) {
		S.Create(SimulationType.AutomaticRollbacks, GameSessionSetup.SessionConfig);
		S.Types().Signal<PlayerConnectedSignal>().Signal<PlayerDisconnectedSignal>();
		GameSessionSetup.Register();
		S.Initialize();
		GameWorldSetup.CreateAndInitialize(level);
		_systemsCreated = true;
		try {
			W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = LocalChannel });
			W.NewEntity(new Player { PlayerGuid = Guid.NewGuid(), InputChannel = RemoteChannel });
			if (level.Navigation is not null) {
				SpawnBenchCharacters();
			}
			S.SaveFrame();
			RollbackObserver = new BenchTickCounter();

			// Warm up, then time regular ticks with every input on time.
			for (var i = 0; i < 240; i++) {
				StepBenchTick();
			}
			const int sampleTicks = 600;
			var plans = 0;
			var sw = Stopwatch.StartNew();
			for (var i = 0; i < sampleTicks; i++) {
				StepBenchTick();
				sw.Stop();
				foreach (var agent in W.Query<All<NavAgent>>().Entities()) {
					plans += agent.Read<NavAgent>().NextRepathTick == S.CurrentTick ? 1 : 0;
				}
				if (i < sampleTicks - 1) {
					sw.Start();
				}
			}
			var tick = sw.Elapsed.TotalMilliseconds / sampleTicks;

			// The remote player's input for the last `ticks` ticks arrives all at once, as after a network
			// stall, and differs from what was predicted, so the session re-simulates that whole window.
			double Burst(int ticks) {
				var best = double.MaxValue;
				for (var run = 0; run < 5; run++) {
					for (var i = 0; i < 5; i++) {
						StepBenchTick();
					}
					var stallStart = S.CurrentTick;
					for (var i = 0; i < ticks; i++) {
						StepBenchTick(remoteOnTime: false);
					}
					var now = S.CurrentTick;
					for (var t = stallStart; t < now; t++) {
						var late = new PlayerInput { MoveX = (t + run) % 40 < 20 ? Fixed64.FP.One : -Fixed64.FP.One };
						if (S.SetApprovedInputAt(t, RemoteChannel, late) != SetResult.Applied) {
							throw new InvalidOperationException($"late input for tick {t} was not applied");
						}
					}
					var resimulatedBefore = _benchResimulatedTicks;
					var burst = Stopwatch.StartNew();
					S.FastForwardToTick(now);
					best = Math.Min(best, burst.Elapsed.TotalMilliseconds);
					if (_benchResimulatedTicks - resimulatedBefore != ticks) {
						throw new InvalidOperationException($"expected a {ticks}-tick re-simulation, got {_benchResimulatedTicks - resimulatedBefore}");
					}
				}
				return best;
			}
			var typical = Burst(TypicalRollbackTicks);
			var full = Burst(NavFullRollbackTicks);
			return new NavigationSceneTiming(tick, typical, full, (double)plans / sampleTicks);
		} finally {
			RollbackObserver = null;
			GameWorldSetup.Destroy();
			_systemsCreated = false;
			S.Destroy();
		}
	}

	/// <summary>Adds characters around the spawned one until there are <see cref="NavBenchCharacters"/>, all re-planning every tick.</summary>
	private static void SpawnBenchCharacters() {
		var res = Systems.GetResource<NavCharacterRes>();
		for (var i = W.Query<All<NavAgent>>().EntitiesCount(); i < NavBenchCharacters; i++) {
			var character = W.NewEntity<NavCharacter>();
			var position = new Fixed64.FVector3(Fixed64.FP.FromRatio(i % 4 * 6 - 9, 1), Fixed64.FP.FromRatio(3, 2), Fixed64.FP.FromRatio(-12 - i / 4 * 5, 1));
			character.Set(new Transform { Position = position, Rotation = Fixed64.FQuaternion.Identity });
			character.Set(NavAgent.Create(res.MoveSpeed, res.ArrivalRadius, SpawnNavCharacterSystem.SnapDistance(Systems.GetResource<NavigationRes>()), res.RepathIntervalTicks));
			character.Set<ChasesNearestPlayer>();
		}
		foreach (var agent in W.Query<All<NavAgent>>().Entities()) {
			agent.Ref<NavAgent>().RepathIntervalTicks = 1;
		}
	}

	private static int _benchResimulatedTicks;

	/// <summary>Counts ticks simulated again, to prove a burst really re-simulated.</summary>
	private sealed class BenchTickCounter : IRollbackObserver {
		private int _lastTick = int.MinValue;

		public void OnTickSimulated(int tick) {
			if (tick <= _lastTick) {
				_benchResimulatedTicks++;
			}
			_lastTick = Math.Max(_lastTick, tick);
		}

		public void OnFullSync() { }
	}

	/// <summary>Both players walk back and forth, so the characters keep changing destination.</summary>
	private static void StepBenchTick(bool remoteOnTime = true) {
		var tick = S.CurrentTick;
		S.SetApprovedInput(LocalChannel, new PlayerInput { MoveX = tick / 90 % 2 == 0 ? Fixed64.FP.One : -Fixed64.FP.One });
		if (remoteOnTime) {
			S.SetApprovedInput(RemoteChannel, new PlayerInput { MoveY = tick / 70 % 2 == 0 ? Fixed64.FP.One : -Fixed64.FP.One });
		}
		S.FastForwardToTick(tick + 1);
	}

	private static LevelData FindSampleLevel() {
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
			var candidate = Path.Combine(directory.FullName, "Client", "maps", "level_pipeline_test.level.bytes");
			if (File.Exists(candidate)) {
				var data = LevelFile.ReadFromDisk(candidate).Data;
				// The sample's crates would add physics cost to both runs; only navigation is measured.
				return new LevelData(data.Entities.Where(static entity => entity.Type == LevelEntityType.StaticGeometry), data.Navigation);
			}
		}
		throw new FileNotFoundException("Could not find Client/maps/level_pipeline_test.level.bytes.");
	}
}
