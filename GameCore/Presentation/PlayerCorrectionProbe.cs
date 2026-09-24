using FFS.Libraries.StaticEcs;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>Observes the simulation from outside the ECS world; see <see cref="Core{TWorld}.RollbackObserver"/>.</summary>
public interface IRollbackObserver {
	/// <summary>Called after every simulated tick, including re-simulated ones. The world now holds the state at <paramref name="tick"/> + 1.</summary>
	void OnTickSimulated(int tick);

	/// <summary>A full sync replaced the world with a new timeline.</summary>
	void OnFullSync();
}

/// <summary>How far a rollback moved a player at the tick the client was showing before it.</summary>
public struct PositionCorrection {
	public ushort Channel;
	/// <summary>Old position minus corrected position: the offset that keeps the player where they were drawn.</summary>
	public FVector3 Error;
	/// <summary>The corrected timeline has a different <see cref="Transform.TeleportTick"/>; a jump that must not be smoothed.</summary>
	public bool Teleported;
}

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Notified after every simulated tick. Static per closed <c>Core&lt;TWorld&gt;</c> like
	/// <see cref="FxSink"/>: null on the server, set by the client.
	/// </summary>
	public static IRollbackObserver? RollbackObserver;

	/// <summary>
	/// Measures how much a client update's rollback moved each player. <see cref="BeginUpdate"/>
	/// records every player's position at the current head tick; when re-simulation passes that
	/// same tick on the corrected timeline, it records them again; <see cref="EndUpdate"/> reports
	/// the differences. An update without a misprediction re-simulates identically and reports
	/// nothing. Players are keyed by input channel, since a rollback can re-create an entity under
	/// a new GID.
	/// </summary>
	public sealed class PlayerCorrectionProbe : IRollbackObserver {
		private readonly Dictionary<ushort, Sample> _before = new();
		private readonly Dictionary<ushort, Sample> _after = new();
		private int _headTick;
		private bool _fullSynced;

		public void BeginUpdate() {
			_headTick = S.CurrentTick;
			_fullSynced = false;
			_after.Clear();
			Capture(_before);
		}

		public void OnTickSimulated(int tick) {
			if (tick + 1 == _headTick) {
				Capture(_after);
			}
		}

		public void OnFullSync() {
			_fullSynced = true;
		}

		/// <summary>
		/// Fills <paramref name="corrections"/> with every player whose position at the old head tick
		/// changed. Returns false when a full sync happened; nothing is comparable across that and
		/// existing offsets should be dropped.
		/// </summary>
		public bool EndUpdate(List<PositionCorrection> corrections) {
			corrections.Clear();
			if (_fullSynced) {
				_before.Clear();
				_after.Clear();
				return false;
			}

			foreach (var (channel, before) in _before) {
				if (!_after.TryGetValue(channel, out var after)) {
					continue;
				}

				var teleported = before.TeleportTick != after.TeleportTick;
				var error = before.Position - after.Position;
				if (teleported || error != FVector3.Zero) {
					corrections.Add(new PositionCorrection { Channel = channel, Error = error, Teleported = teleported });
				}
			}
			return true;
		}

		private static void Capture(Dictionary<ushort, Sample> samples) {
			samples.Clear();
			foreach (var player in W.Query<All<PlayerInfo, Transform>>().Entities()) {
				ref readonly var transform = ref player.Read<Transform>();
				samples[player.Read<PlayerInfo>().InputChannel] = new Sample(transform.Position, transform.TeleportTick);
			}
		}

		private readonly record struct Sample(FVector3 Position, int TeleportTick);
	}
}
