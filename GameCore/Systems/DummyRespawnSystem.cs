using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed64;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// On a Dummy's <see cref="DeadEvent"/>, starts a <see cref="PendingRespawn"/> timer for its
	/// <see cref="RailSlot"/>; once it elapses, recreates the Dummy at that slot via
	/// <see cref="SpawnDummySystem.SpawnAt"/>. Registered before <see cref="DeathSystem"/> (order 0
	/// vs. 1) so it reads <see cref="RailSlot"/> off the dying entity before that system destroys it.
	/// </summary>
	public struct DummyRespawnSystem : ISystem {
		private EventReceiver<TWorld, DeadEvent> deadReceiver;

		public void Init() {
			deadReceiver = W.RegisterEventReceiver<DeadEvent>();
		}

		public void Update() {
			foreach (var e in deadReceiver) {
				if (!e.Value.Gid.TryUnpack<TWorld>(out var entity) || !entity.Has<RailSlot>()) {
					continue;
				}

				var slotIndex = entity.Read<RailSlot>()!.Index;
				var delay = Systems.GetResource<DummyRes>().RespawnDelay;
				W.NewEntity<Default>().Set(new PendingRespawn { SlotIndex = slotIndex, TimeRemaining = delay });
			}

			W.Query().For(static (W.Entity entity, ref PendingRespawn pending) => {
				pending.TimeRemaining -= Const.DeltaTime;
				if (pending.TimeRemaining > FP.Zero) {
					return;
				}

				SpawnDummySystem.SpawnAt(pending.SlotIndex);
				entity.Destroy();
			});
		}
	}
}
