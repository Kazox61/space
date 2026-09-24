using System.Collections.Generic;
using System.Diagnostics;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>Simulation phases timed by <see cref="PhysicsStepStats"/>.</summary>
public enum PhysicsPhase : byte {
	ProxyUpdate,
	PairUpdate,
	Narrowphase,
	SolverPrepare,
	Solve,
	Continuous,
	Finalize,
	Count,
}

/// <summary>
/// Counters and wall-clock timings for the most recent physics tick. Diagnostic only: timings are
/// non-deterministic and nothing here feeds back into simulation state or snapshots.
/// </summary>
public struct PhysicsStepStats {
	public int AwakeBodies;
	public int SleepingBodies;
	public int MovedProxies;
	public int PairsCreated;
	public int ContactsUpdated;
	public int ContactsSleeping;
	public int TouchingContacts;
	public int Constraints;
	public int ContinuousBodies;
	public int ContinuousHits;
	public int Islands;
	public int IslandsFellAsleep;
	public int BodiesWoken;

	internal PhaseTimes Ticks;

	/// <summary>Elapsed time of one phase during the last tick, in milliseconds.</summary>
	public readonly double Milliseconds(PhysicsPhase phase) => Ticks[(int)phase] * 1000.0 / Stopwatch.Frequency;

	/// <summary>Sum of every timed phase during the last tick, in milliseconds.</summary>
	public readonly double TotalMilliseconds {
		get {
			long total = 0;
			for (var i = 0; i < (int)PhysicsPhase.Count; i++) {
				total += Ticks[i];
			}
			return total * 1000.0 / Stopwatch.Frequency;
		}
	}

	public override readonly string ToString() =>
		$"awake={AwakeBodies} sleeping={SleepingBodies} moved={MovedProxies} newPairs={PairsCreated} "
		+ $"narrow={ContactsUpdated} sleepingContacts={ContactsSleeping} touching={TouchingContacts} constraints={Constraints} "
		+ $"ccd={ContinuousBodies}/{ContinuousHits} islands={Islands} fellAsleep={IslandsFellAsleep} woken={BodiesWoken} "
		+ $"time={TotalMilliseconds:F3}ms";

	[System.Runtime.CompilerServices.InlineArray((int)PhysicsPhase.Count)]
	internal struct PhaseTimes {
		private long _element0;
	}
}

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Per-world transient physics state: reusable scratch buffers and the last tick's
	/// <see cref="PhysicsStepStats"/>. Deliberately not serialized (no snapshot GUID) -- nothing here
	/// outlives a single system update, so rollback and full synchronization never need it. Being a
	/// world resource rather than static storage keeps every world type and every nested query
	/// independent: scratch lists are rented, so a query callback may issue another query safely.
	/// </summary>
	public sealed class PhysicsRuntime : IResource {
		private readonly Stack<List<EntityGID>> _gidLists = new();
		private readonly Stack<List<W.Entity>> _entityLists = new();

		/// <summary>Statistics for the most recently completed solver step.</summary>
		public PhysicsStepStats Stats;

		/// <summary>Statistics accumulating toward the next completed step, including gameplay wakes before it.</summary>
		internal PhysicsStepStats Pending;

		internal readonly List<W.Entity> SolverBodies = new();
		internal readonly List<ContactSolverSystem.ContactConstraint> SolverConstraints = new();
		internal readonly List<EntityGID> ContinuousCandidates = new();
		internal readonly List<(W.Entity, W.Entity)> IslandEdges = new();
		internal readonly List<int> IslandParents = new();
		internal readonly List<FP> IslandSleepTimes = new();
		internal readonly Dictionary<W.Entity, int> BodyIndices = new();

		public static PhysicsRuntime Get() {
			if (!W.HasResource<PhysicsRuntime>()) {
				W.SetResource(new PhysicsRuntime());
			}
			return W.GetResource<PhysicsRuntime>();
		}

		internal List<EntityGID> RentGidList() => _gidLists.Count > 0 ? _gidLists.Pop() : new List<EntityGID>();

		internal void Return(List<EntityGID> list) {
			list.Clear();
			_gidLists.Push(list);
		}

		internal List<W.Entity> RentEntityList() => _entityLists.Count > 0 ? _entityLists.Pop() : new List<W.Entity>();

		internal void Return(List<W.Entity> list) {
			list.Clear();
			_entityLists.Push(list);
		}

		/// <summary>Publishes the accumulated statistics. Called at the end of the solver step.</summary>
		internal void CompleteTick() {
			Stats = Pending;
			Pending = default;
		}

		internal static long Timestamp() => Stopwatch.GetTimestamp();

		internal void AddTime(PhysicsPhase phase, long startTimestamp) {
			Pending.Ticks[(int)phase] += Stopwatch.GetTimestamp() - startTimestamp;
		}
	}
}
