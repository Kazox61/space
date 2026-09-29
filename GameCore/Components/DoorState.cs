using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;

namespace Space.GameCore;

/// <summary>
/// A door's motion and target, set by <c>LevelLoader</c> and toggled by pressure plates. The door is a
/// kinematic body; <see cref="Core{TWorld}.DoorSystem"/> steers its velocity toward the closed or open
/// position and keeps its navigation zone blocked unless it is fully open.
/// </summary>
public struct DoorState : IComponent {
	/// <summary>
	/// The linked zone: its index among the level's zone ids in ordinal order, which is its index in
	/// <see cref="LevelNavigation.Zones"/> and its <see cref="NavZoneState.Zone"/> when the level has navigation.
	/// </summary>
	public int Zone;

	public FPos ClosedPosition;

	/// <summary>World-space offset from <see cref="ClosedPosition"/> to the open position.</summary>
	public FVector3 OpenOffset;

	public FP Speed;

	/// <summary>Where the door is heading: open or closed.</summary>
	public bool Open;

	/// <summary>The door reached its open position; only then is its zone unblocked.</summary>
	public bool FullyOpen;
}
