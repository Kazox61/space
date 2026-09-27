using System.Runtime.CompilerServices;
using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public enum NavAgentStatus : byte {
	/// <summary>No destination, or one that has not been planned yet.</summary>
	Idle,
	Moving,
	Arrived,

	/// <summary>The last plan failed (see <see cref="NavAgent.PathStatus"/>); retried on the next repath.</summary>
	Failed,
}

/// <summary>Inline triangle corridor, so <see cref="NavAgent"/> stays unmanaged and is snapshotted by value.</summary>
[InlineArray(NavAgent.CorridorCapacity)]
public struct NavCorridor {
	private int _element0;
}

/// <summary>
/// Path-following state for a <see cref="Mover"/>-driven character. Set <see cref="Destination"/>
/// with <see cref="SetDestination"/>; <c>NavAgentSystem</c> plans a corridor and writes the
/// character's <see cref="CharacterMoveIntent"/> toward the next funnel corner. Every field that
/// carries path state lives here, so rollback restores it with the entity.
/// </summary>
public struct NavAgent : IComponent {
	/// <summary>Longest corridor kept per plan. A longer route keeps its start side and is re-planned from its end.</summary>
	public const int CorridorCapacity = 64;

	/// <summary>Horizontal speed in world units per second.</summary>
	public FP Speed;

	/// <summary>XZ distance to the destination at which the agent stops.</summary>
	public FP ArrivalRadius;

	/// <summary>How far an off-mesh destination may be snapped onto passable ground.</summary>
	public FP DestinationSnapDistance;

	/// <summary>Ticks between periodic re-plans while the agent has a destination.</summary>
	public int RepathIntervalTicks;

	/// <summary>Navigation areas the agent may enter (see <see cref="NavTriangleArea.AreaMask"/>).</summary>
	public int AreaMask;

	/// <summary>Requested destination; only meaningful while <see cref="HasDestination"/>.</summary>
	public FVector3 Destination;

	public bool HasDestination;

	public NavAgentStatus Status;

	/// <summary>Result of the last plan.</summary>
	public NavPathStatus PathStatus;

	/// <summary><see cref="Destination"/> as it was when the current corridor was planned. A difference forces a re-plan.</summary>
	public FVector3 PlannedDestination;

	/// <summary>
	/// Where the corridor ends: the destination snapped onto the mesh, or the centroid of the
	/// corridor's last triangle when the route was truncated.
	/// </summary>
	public FVector3 PathTarget;

	/// <summary>Triangle the agent was located in this tick, or -1.</summary>
	public int CurrentTriangle;

	/// <summary>Simulation tick at or after which the agent re-plans.</summary>
	public int NextRepathTick;

	/// <summary>
	/// <see cref="NavigationRes.ZoneSignature"/> when the agent last planned. A different signature
	/// means a zone opened, closed, or changed cost, and forces a re-plan.
	/// </summary>
	public ulong PlannedZoneSignature;

	/// <summary>Index into <see cref="Corridor"/> of <see cref="CurrentTriangle"/>.</summary>
	public int CorridorIndex;

	public int CorridorLength;

	public NavCorridor Corridor;

	public static NavAgent Create(FP speed, FP arrivalRadius, FP destinationSnapDistance, int repathIntervalTicks, int areaMask = ~0) {
		if (speed <= FP.Zero) {
			throw new ArgumentOutOfRangeException(nameof(speed), "Must be positive.");
		}
		if (arrivalRadius < FP.Zero || destinationSnapDistance < FP.Zero) {
			throw new ArgumentOutOfRangeException(nameof(arrivalRadius), "Distances must not be negative.");
		}
		if (repathIntervalTicks <= 0) {
			throw new ArgumentOutOfRangeException(nameof(repathIntervalTicks), "Must be positive.");
		}
		return new NavAgent {
			Speed = speed,
			ArrivalRadius = arrivalRadius,
			DestinationSnapDistance = destinationSnapDistance,
			RepathIntervalTicks = repathIntervalTicks,
			AreaMask = areaMask,
			CurrentTriangle = -1,
		};
	}

	/// <summary>Requests a path to <paramref name="destination"/>; any bit-level change re-plans on the next update.</summary>
	public void SetDestination(FVector3 destination) {
		Destination = destination;
		HasDestination = true;
	}

	/// <summary>Drops the destination and the corridor; the agent stands still.</summary>
	public void ClearDestination() {
		HasDestination = false;
		Status = NavAgentStatus.Idle;
		CorridorIndex = 0;
		CorridorLength = 0;
	}
}
