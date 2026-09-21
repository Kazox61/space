using FFS.Libraries.StaticEcs;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Counts down every <see cref="Lifetime"/> holder and kills it at zero through
	/// <see cref="DeadEvent"/>, so expiry reuses <c>DeathSystem</c>'s complete body/shape/proxy
	/// teardown. Registered at order 0, before <c>DeathSystem</c> (order 1), so expiry and
	/// destruction land in the same tick. The event is sent exactly once -- the countdown stops
	/// once it reaches zero.
	/// </summary>
	public struct ProjectileDespawnSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity entity, ref Lifetime lifetime) => {
				if (lifetime.TimeRemaining <= FP.Zero) {
					// Already expired at spawn (or restored expired): never crosses zero via the
					// countdown below, so die immediately rather than live forever.
					W.SendEvent(new DeadEvent { Gid = entity.GID });
					return;
				}

				lifetime.TimeRemaining -= Const.DeltaTime;
				if (lifetime.TimeRemaining <= FP.Zero) {
					W.SendEvent(new DeadEvent { Gid = entity.GID });
				}
			});
		}
	}
}
