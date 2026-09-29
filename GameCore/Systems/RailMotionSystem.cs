using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Drives every <see cref="PatrolRail"/> body between its stops. A platform-linked navigation
	/// zone is open only while the platform is aligned with its authored start floor.
	/// </summary>
	public struct RailMotionSystem : ISystem {
		public const int EndpointDwellSeconds = 3;
		private static readonly FP ArrivalTolerance = FP.FromRatio(1, 1000);

		public void Update() {
			W.Query<All<PatrolRail>>().For(static (W.Entity entity, ref Body body) => {
				ref var rail = ref entity.Ref<PatrolRail>();
				if (rail.DwellTicksRemaining > 0) {
					rail.DwellTicksRemaining--;
					if (body.LinearVelocity != FVector3.Zero) {
						BodyOperations.SetLinearVelocity(entity, FVector3.Zero);
					}
					return;
				}

				var target = rail.MovingToEnd ? rail.End : rail.Start;
				var remaining = target - body.Transform.Position;
				var distance = FVector3.Length(remaining);
				if (distance <= ArrivalTolerance) {
					rail.MovingToEnd = !rail.MovingToEnd;
					rail.DwellTicksRemaining = EndpointDwellSeconds * S.TickRate - 1;
					if (body.LinearVelocity != FVector3.Zero) {
						BodyOperations.SetLinearVelocity(entity, FVector3.Zero);
					}
					return;
				}

				var velocity = distance <= rail.Speed * Const.DeltaTime.To32()
					? remaining * Const.InvDeltaTime.To32()
					: remaining * (rail.Speed / distance);
				if (velocity != body.LinearVelocity) {
					BodyOperations.SetLinearVelocity(entity, velocity);
				}
			});

			Span<byte> platformState = stackalloc byte[NavZoneData.MaxZones];
			foreach (var platform in W.Query<All<PatrolRail, Body>>().Entities()) {
				ref readonly var rail = ref platform.Read<PatrolRail>();
				if ((uint)rail.Zone >= (uint)NavZoneData.MaxZones) {
					continue;
				}
				platformState[rail.Zone] |= 1;
				ref readonly var body = ref platform.Read<Body>();
				var nextPosition = body.Transform.Position + Const.DeltaTime.To32() * body.LinearVelocity;
				if (FVector3.Length(nextPosition - rail.Start) > ArrivalTolerance) {
					platformState[rail.Zone] |= 2;
				}
			}

			foreach (var zoneEntity in W.Query<All<NavZoneState>>().Entities()) {
				ref var zone = ref zoneEntity.Ref<NavZoneState>();
				if ((uint)zone.Zone < (uint)NavZoneData.MaxZones && (platformState[zone.Zone] & 1) != 0) {
					zone.Blocked = (platformState[zone.Zone] & 2) != 0;
				}
			}
		}
	}
}
