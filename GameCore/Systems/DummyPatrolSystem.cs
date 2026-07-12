using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Bounces every <see cref="PatrolRail"/> body between its rail ends by flipping
	/// <see cref="Body.LinearVelocity"/>'s X sign on overshoot -- same pattern as box3d's
	/// SensorHits sample (kinematic sensor reversing direction at a position threshold). Runs
	/// before <see cref="ContactSolverSystem"/> so the flipped velocity is what gets integrated
	/// this tick.
	/// </summary>
	public struct DummyPatrolSystem : ISystem {
		public void Update() {
			// All<PatrolRail>: a plain `ref Body` param alone would also match Projectile (any Body
			// owner), which has no PatrolRail to Read.
			W.Query<All<PatrolRail>>().For(static (W.Entity entity, ref Body body) => {
				ref readonly var rail = ref entity.Read<PatrolRail>();

				var x = body.Transform.Position.X;
				var speed = FP.Abs(body.LinearVelocity.X);

				if (x < rail.Min) {
					body.LinearVelocity.X = speed;
				} else if (x > rail.Max) {
					body.LinearVelocity.X = -speed;
				}
			});
		}
	}
}
