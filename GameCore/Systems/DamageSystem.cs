using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;


public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct DamageSystem : ISystem {

		private EventReceiver<TWorld, DamageEvent> damageReceiver;

		public void Init() {
			damageReceiver = W.RegisterEventReceiver<DamageEvent>();
		}

		public void Update() {
			foreach (var e in damageReceiver) {
				if (e.Value.Target.TryUnpack<TWorld>(out var target)) {
					ref var health = ref target.Ref<Health>();
					health.Value -= e.Value.Amount;
					if (health.Value <= 0) {
						W.SendEvent(new DeadEvent { Gid = e.Value.Target });
					}
				}
			}
		}
	}
}
