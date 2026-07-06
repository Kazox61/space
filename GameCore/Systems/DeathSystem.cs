using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct DeathSystem : ISystem {
		private EventReceiver<TWorld, DeadEvent> deadCleaner;

		public void Init() {
			deadCleaner = W.RegisterEventReceiver<DeadEvent>();
		}

		public void Update() {
			foreach (var e in deadCleaner.LastOnly()) {
				if (e.Value.Gid.TryUnpack<TWorld>(out var entity)) {
					entity.Destroy();
				}
			}
		}
	}
}
