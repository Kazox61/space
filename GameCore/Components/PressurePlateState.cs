using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A pressure plate's link and occupancy. Stepping onto an empty plate toggles every door of
/// <see cref="Zone"/>; see <see cref="Core{TWorld}.PressurePlateSystem"/>.
/// </summary>
public struct PressurePlateState : IComponent {
	/// <summary>The linked zone, numbered like <see cref="DoorState.Zone"/>.</summary>
	public int Zone;

	/// <summary>Character sensor proxies touching the plate.</summary>
	public int Occupants;
}
