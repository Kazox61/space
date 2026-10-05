using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Sets each <see cref="ChasesNearestPlayer"/> agent's destination to the feet of the nearest
	/// player (XZ distance, ties to the lower input channel), or clears it when no player exists.
	/// The destination only moves once the player is <see cref="NavCharacterRes.RetargetDistance"/>
	/// away from it, so a walking player does not force a re-plan every tick.
	/// </summary>
	public struct NavChaseSystem : ISystem {
		public void Update() {
			var retargetDistance = Systems.GetResource<NavCharacterRes>().RetargetDistance;

			foreach (var chaser in W.Query<All<ChasesNearestPlayer, NavAgent, Transform>>().Entities()) {
				var position = NavGeometry.ToXZ(chaser.Read<Transform>().Position);
				var found = false;
				var bestDistanceSqr = FP.Zero;
				var bestChannel = 0;
				var bestFeet = FVector3.Zero;

				foreach (var player in W.Query<All<PlayerInfo, Transform, Mover>>().Entities()) {
					ref readonly var playerTransform = ref player.Read<Transform>();
					var channel = (int)player.Read<PlayerInfo>().InputChannel;
					var distanceSqr = FVector2.DistanceSqr(position, NavGeometry.ToXZ(playerTransform.Position));
					if (found && (distanceSqr > bestDistanceSqr || (distanceSqr == bestDistanceSqr && channel > bestChannel))) {
						continue;
					}
					ref readonly var mover = ref player.Read<Mover>();
					found = true;
					bestDistanceSqr = distanceSqr;
					bestChannel = channel;
					bestFeet = playerTransform.Position + FVector3.Up * (mover.CapsuleCenter1.Y - mover.CapsuleRadius).To64();
				}

				ref var agent = ref chaser.Ref<NavAgent>();
				if (!found) {
					agent.ClearDestination();
				} else if (!agent.HasDestination || FVector2.DistanceSqr(NavGeometry.ToXZ(agent.Destination), NavGeometry.ToXZ(bestFeet)) > retargetDistance * retargetDistance) {
					agent.SetDestination(bestFeet);
				}
			}
		}
	}
}
