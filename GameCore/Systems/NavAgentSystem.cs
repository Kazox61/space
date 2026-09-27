using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Plans and follows each <see cref="NavAgent"/>'s path and writes its
	/// <see cref="CharacterMoveIntent"/>; <see cref="CharacterMoverSystem"/> then moves it, so collision,
	/// gravity and grounding are exactly the player's.
	///
	/// Each tick the agent is located on the mesh (the nearest mesh point when it stands off it) and
	/// its progress along the corridor is updated. It re-plans when the destination changed, when a
	/// navigation zone changed (<see cref="NavigationRes.ZoneSignature"/>), when it left its corridor,
	/// when a truncated corridor ran out, and every <see cref="NavAgent.RepathIntervalTicks"/>. Otherwise it steers straight at the next funnel
	/// corner, never faster than would carry it past that corner within one tick.
	/// </summary>
	public struct NavAgentSystem : ISystem {
		public void Update() {
			var navigation = Systems.GetResource<NavigationRes>();
			var tick = S.CurrentTick;
			// More than one slot, so FindCorners can drop corners the agent already stands on and the
			// agent steers at the next one instead of stalling on it.
			Span<FVector3> corners = stackalloc FVector3[3];

			foreach (var entity in W.Query<All<NavAgent, Transform, Mover, CharacterMoveIntent>>().Entities()) {
				ref var agent = ref entity.Ref<NavAgent>();
				ref var intent = ref entity.Ref<CharacterMoveIntent>();
				intent = default;

				if (!navigation.HasMesh || !agent.HasDestination) {
					agent.ClearDestination();
					agent.CurrentTriangle = -1;
					continue;
				}

				var feet = Feet(entity.Read<Transform>(), entity.Read<Mover>());

				var onMesh = Locate(navigation.Query!, feet, out var located);
				agent.CurrentTriangle = located;
				if (located < 0) {
					Fail(ref agent, NavPathStatus.StartOffMesh, tick, navigation.ZoneSignature);
					continue;
				}

				if (NeedsPlan(ref agent, located, tick, navigation.ZoneSignature)) {
					Plan(navigation, ref agent, onMesh, located, tick);
				}
				if (agent.Status != NavAgentStatus.Moving) {
					continue;
				}

				var targetXZ = NavGeometry.ToXZ(agent.PathTarget);
				var inLastTriangle = agent.CorridorIndex == agent.CorridorLength - 1;
				if (inLastTriangle && agent.PathStatus == NavPathStatus.Found
					&& FVector2.DistanceSqr(NavGeometry.ToXZ(feet), targetXZ) <= agent.ArrivalRadius * agent.ArrivalRadius) {
					agent.Status = NavAgentStatus.Arrived;
					continue;
				}

				ReadOnlySpan<int> corridor = agent.Corridor;
				var count = navigation.Funnel!.FindCorners(corridor[agent.CorridorIndex..agent.CorridorLength], onMesh, agent.PathTarget, corners);
				if (count == 0) {
					Fail(ref agent, NavPathStatus.NoRoute, tick, navigation.ZoneSignature);
					continue;
				}

				var toCorner = NavGeometry.ToXZ(corners[0]) - NavGeometry.ToXZ(feet);
				var distance = FVector2.Length(toCorner);
				if (distance <= FP.CalculationsEpsilon) {
					continue;
				}
				var speed = FP.Min(agent.Speed, distance * Const.InvDeltaTime);
				intent.Velocity = toCorner / distance * speed;
			}
		}

		/// <summary>Bottom of the character's capsule, the point the agent is located and steered by.</summary>
		internal static FVector3 Feet(in Transform transform, in Mover mover) {
			return new FVector3(transform.Position.X, transform.Position.Y + (mover.CapsuleCenter1.Y - mover.CapsuleRadius).To64(), transform.Position.Z);
		}

		/// <summary>
		/// The triangle under <paramref name="feet"/>, or the nearest one when the agent stands off the
		/// mesh; returns the matching point on the surface.
		/// </summary>
		internal static FVector3 Locate(NavMeshQuery query, FVector3 feet, out int triangle) {
			var xz = NavGeometry.ToXZ(feet);
			triangle = query.FindTriangle(xz, feet.Y);
			if (triangle < 0) {
				xz = query.ClosestPoint(xz, out triangle);
				if (triangle < 0) {
					return feet;
				}
			}
			return new FVector3(xz.X, query.SampleHeight(xz, triangle), xz.Y);
		}

		/// <summary>Updates corridor progress and reports whether the agent must re-plan.</summary>
		private static bool NeedsPlan(ref NavAgent agent, int triangle, int tick, ulong zoneSignature) {
			if (agent.Status == NavAgentStatus.Idle || !agent.Destination.Equals(agent.PlannedDestination) || tick >= agent.NextRepathTick
				|| agent.PlannedZoneSignature != zoneSignature) {
				return true;
			}
			if (agent.Status != NavAgentStatus.Moving) {
				return false;
			}

			ReadOnlySpan<int> corridor = agent.Corridor;
			var index = corridor[..agent.CorridorLength].IndexOf(triangle);
			if (index < 0) {
				return true;
			}
			agent.CorridorIndex = index;
			// A truncated corridor is planned again from its end rather than finished.
			return index == agent.CorridorLength - 1 && agent.PathStatus == NavPathStatus.FoundTruncated;
		}

		private static void Plan(NavigationRes navigation, ref NavAgent agent, FVector3 start, int startTriangle, int tick) {
			var query = navigation.Query!;
			var zoneSignature = navigation.ZoneSignature;
			agent.PlannedDestination = agent.Destination;
			agent.PlannedZoneSignature = zoneSignature;
			agent.NextRepathTick = tick + agent.RepathIntervalTicks;

			var destinationXZ = NavGeometry.ToXZ(agent.Destination);
			var endTriangle = query.FindPassableTriangleForEndpoint(destinationXZ, agent.Destination.Y, agent.AreaMask);
			if (endTriangle < 0) {
				destinationXZ = query.ProjectToPassable(destinationXZ, agent.DestinationSnapDistance, agent.AreaMask, out endTriangle);
				if (endTriangle < 0) {
					Fail(ref agent, NavPathStatus.EndOffMesh, tick, zoneSignature);
					return;
				}
			}
			var end = new FVector3(destinationXZ.X, query.SampleHeight(destinationXZ, endTriangle), destinationXZ.Y);

			Span<int> corridor = agent.Corridor;
			var status = navigation.Pathfinder!.FindPath(start, startTriangle, end, endTriangle, agent.AreaMask, corridor, out var length);
			agent.PathStatus = status;
			if (!status.IsFound()) {
				Fail(ref agent, status, tick, zoneSignature);
				return;
			}

			agent.CorridorIndex = 0;
			agent.CorridorLength = length;
			if (status == NavPathStatus.FoundTruncated) {
				ref readonly var last = ref navigation.Mesh!.Triangles[corridor[length - 1]];
				agent.PathTarget = new FVector3(last.CenterXZ.X, last.CenterY, last.CenterXZ.Y);
			} else {
				agent.PathTarget = end;
			}
			agent.Status = NavAgentStatus.Moving;
		}

		/// <summary>Stands still until the destination or a zone changes, or the repath interval passes.</summary>
		private static void Fail(ref NavAgent agent, NavPathStatus status, int tick, ulong zoneSignature) {
			agent.Status = NavAgentStatus.Failed;
			agent.PathStatus = status;
			agent.PlannedDestination = agent.Destination;
			agent.PlannedZoneSignature = zoneSignature;
			agent.NextRepathTick = tick + agent.RepathIntervalTicks;
			agent.CorridorIndex = 0;
			agent.CorridorLength = 0;
		}
	}
}
