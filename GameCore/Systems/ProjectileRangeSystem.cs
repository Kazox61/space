using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Limits each projectile's next physics step to its remaining range. The final step is shortened
	/// to end exactly at zero, then normal death processing removes the projectile before another step.
	/// </summary>
	public struct ProjectileRangeSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity entity, ref Body body, ref ProjectileRange range) => {
				if (range.Remaining <= FP.Zero) {
					body.LinearVelocity = FVector3.Zero;
					if (!range.ExpirySent) {
						range.ExpirySent = true;
						W.SendEvent(new DeadEvent { Gid = entity.GID });
					}
					return;
				}

				var stepDistance = FVector3.Length(body.LinearVelocity) * Const.DeltaTime.To32();
				if (stepDistance <= FP.Zero) {
					return;
				}
				if (stepDistance < range.Remaining) {
					range.Remaining -= stepDistance;
					return;
				}

				body.LinearVelocity *= range.Remaining / stepDistance;
				range.Remaining = FP.Zero;
				range.ExpirySent = true;
				W.SendEvent(new DeadEvent { Gid = entity.GID });
			});
		}
	}
}
