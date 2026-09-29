using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Counts the character <see cref="SensorProxy"/> capsules on each pressure plate from sensor
	/// begin/end events, and toggles every door of the plate's zone when the first one steps on.
	/// Runs after <see cref="ContactSystem"/> in the same tick. The occupancy lives in
	/// <see cref="PressurePlateState"/> and the touching state in the snapshotted contact, so a
	/// re-simulated tick sends and counts the same events again.
	/// </summary>
	public struct PressurePlateSystem : ISystem {
		private EventReceiver<TWorld, SensorBeginTouchEvent> _beginReceiver;
		private EventReceiver<TWorld, SensorEndTouchEvent> _endReceiver;

		public void Init() {
			_beginReceiver = W.RegisterEventReceiver<SensorBeginTouchEvent>();
			_endReceiver = W.RegisterEventReceiver<SensorEndTouchEvent>();
		}

		public void Update() {
			// Begins before ends: a plate one character leaves as another arrives never counts as empty.
			foreach (var e in _beginReceiver) {
				if (TryResolvePlate(e.Value.SensorShape, e.Value.VisitorShape, out var plate) && plate.Has<PressurePlateState>()) {
					ref var state = ref plate.Ref<PressurePlateState>();
					state.Occupants++;
					if (state.Occupants == 1) {
						ToggleDoors(state.Zone);
					}
				}
			}
			foreach (var e in _endReceiver) {
				if (TryResolvePlate(e.Value.SensorShape, e.Value.VisitorShape, out var plate) && plate.Has<PressurePlateState>()) {
					ref var state = ref plate.Ref<PressurePlateState>();
					state.Occupants = Math.Max(0, state.Occupants - 1);
				}
			}
		}

		private static void ToggleDoors(int zone) {
			foreach (var door in W.Query<All<DoorState>>().Entities()) {
				ref var state = ref door.Ref<DoorState>();
				if (state.Zone == zone) {
					state.Open = !state.Open;
				}
			}
		}

		/// <summary>The sensor's owner, when the visitor is a character's <see cref="SensorProxy"/>.</summary>
		private static bool TryResolvePlate(EntityGID sensorShape, EntityGID visitorShape, out W.Entity plate) {
			return TryResolveOwner(sensorShape, out plate)
				&& TryResolveOwner(visitorShape, out var visitor)
				&& visitor.Has<SensorProxy>();
		}

		private static bool TryResolveOwner(EntityGID shapeGid, out W.Entity owner) {
			owner = default;
			return shapeGid.TryUnpack<TWorld>(out var shapeEntity)
				&& shapeEntity.Has<W.Link<BodyOwner>>()
				&& shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack(out owner);
		}
	}
}
