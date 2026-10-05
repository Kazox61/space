using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Steers each door's kinematic body toward its open or closed position at its speed (the solver
	/// integrates the velocity, so the door pushes whatever is in its way), and blocks each door's
	/// <see cref="NavZoneState"/> unless every door of the zone is fully open. Runs before
	/// <see cref="NavZoneApplySystem"/> so agents plan against this tick's doors.
	/// </summary>
	public struct DoorSystem : ISystem {
		private static readonly FP ArrivalTolerance = FP.FromRatio(1, 1000);

		public void Update() {
			W.Query<All<DoorState>>().For(static (W.Entity entity, ref Body body) => {
				ref var door = ref entity.Ref<DoorState>();
				var target = door.Open ? door.ClosedPosition + door.OpenOffset : door.ClosedPosition;
				var remaining = target - body.Transform.Position;
				var arrived = FP.Abs(remaining.X) <= ArrivalTolerance
					&& FP.Abs(remaining.Y) <= ArrivalTolerance
					&& FP.Abs(remaining.Z) <= ArrivalTolerance;
				door.FullyOpen = door.Open && arrived;

				var velocity = FVector3.Zero;
				if (!arrived) {
					// The last step lands exactly on the target instead of overshooting it.
					var distance = FVector3.Length(remaining);
					velocity = distance <= door.Speed * Const.DeltaTime.To32()
						? remaining * Const.InvDeltaTime.To32()
						: remaining * (door.Speed / distance);
				}
				if (velocity != body.LinearVelocity) {
					BodyOperations.SetLinearVelocity(entity, velocity);
				}
			});

			foreach (var zoneEntity in W.Query<All<NavZoneState>>().Entities()) {
				ref var zone = ref zoneEntity.Ref<NavZoneState>();
				var hasDoor = false;
				var blocked = false;
				foreach (var doorEntity in W.Query<All<DoorState>>().Entities()) {
					ref readonly var door = ref doorEntity.Read<DoorState>();
					if (door.Zone == zone.Zone) {
						hasDoor = true;
						blocked |= !door.FullyOpen;
					}
				}
				if (hasDoor) {
					zone.Blocked = blocked;
				}
			}
		}
	}
}
