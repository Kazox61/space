namespace Space.GameCore;

/// <summary>Plays a deduplicated effect. <paramref name="ageTicks"/> is how many ticks ago it happened (0 = the tick just simulated).</summary>
public interface IFxPlayer {
	void Play(in FxEvent fx, int ageTicks);
}

/// <summary>
/// The client's <see cref="IFxSink"/>: collects effects while ticks are simulated and plays each
/// event once, however often its tick is re-simulated. A played effect is never retracted, even if a
/// correction shows it never happened. See docs/rollback-presentation-events.md §4.3.
/// <para>
/// <see cref="Record"/> runs inside the simulation (possibly many re-simulated ticks per frame);
/// <see cref="Flush"/> runs once afterwards, when the simulated head tick is known.
/// </para>
/// </summary>
public sealed class FxLog : IFxSink {
	/// <summary>Cosmetic effects older than this (150 ms at 60 Hz) are skipped rather than played late.</summary>
	public const int CosmeticLateTicks = 9;
	private const int PruneMargin = 10;

	private readonly Dictionary<FxKey, int> _seen = new();
	private readonly List<Pending> _pending = new();
	private readonly List<FxKey> _expired = new();
	private readonly int _rollbackTicks;

	/// <param name="rollbackTicks">How far back a rollback can re-simulate (<c>S.RollbackTicksCapacity</c>).</param>
	public FxLog(int rollbackTicks) {
		_rollbackTicks = rollbackTicks;
	}

	/// <summary>Input channel of the local player; effects from other channels count as remote.</summary>
	public ushort LocalChannel { get; set; }

	/// <summary>Keys older than this many ticks behind the head are forgotten; no rollback reaches that far back.</summary>
	public int PruneWindow => _rollbackTicks + PruneMargin;

	public int SeenCount => _seen.Count;

	public void Record(in FxEvent fx, int tick) {
		var key = new FxKey(fx.Kind, fx.Channel, fx.KeyTick);
		if (_seen.TryGetValue(key, out var seenTick)) {
			// Keep the latest tick the key was reported on, so pruning never drops a key a rollback
			// could still reach.
			if (tick > seenTick) {
				_seen[key] = tick;
			}
			return;
		}

		_seen.Add(key, tick);
		_pending.Add(new Pending(fx, tick));
	}

	/// <summary>
	/// Plays every event recorded since the last flush unless it is too late for its kind, then
	/// forgets keys outside <see cref="PruneWindow"/>. <paramref name="headTick"/> is the session's
	/// current tick after simulating (<c>S.CurrentTick</c>), which may be short of the target when
	/// the simulation hit its time budget.
	/// </summary>
	public void Flush(int headTick, IFxPlayer player) {
		var lastSimulatedTick = headTick - 1;
		foreach (var pending in _pending) {
			var age = Math.Max(0, lastSimulatedTick - pending.Tick);
			if (age <= LateTicks(pending.Fx)) {
				player.Play(pending.Fx, age);
			}
		}
		_pending.Clear();

		var oldest = headTick - PruneWindow;
		foreach (var (key, tick) in _seen) {
			if (tick < oldest) {
				_expired.Add(key);
			}
		}
		foreach (var key in _expired) {
			_seen.Remove(key);
		}
		_expired.Clear();
	}

	public void Clear() {
		_seen.Clear();
		_pending.Clear();
	}

	/// <summary>
	/// A remote attack only reaches us through a correction -- after the remote player's send
	/// delay, the server relay and our prediction lead -- and it tells the player an enemy fired,
	/// so it plays however late it is (up to the rollback window). Everything else is cosmetic.
	/// Always smaller than <see cref="PruneWindow"/>, or a pruned key could play twice.
	/// </summary>
	public int LateTicks(in FxEvent fx) =>
		fx.Kind == FxKind.AttackStarted && fx.Channel != LocalChannel ? _rollbackTicks : CosmeticLateTicks;

	private readonly record struct FxKey(FxKind Kind, ushort Channel, int KeyTick);

	private readonly record struct Pending(FxEvent Fx, int Tick);
}
