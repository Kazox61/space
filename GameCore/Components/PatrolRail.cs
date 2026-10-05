using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Marks a kinematic <see cref="Body"/> as sliding between two world-space stops. A non-negative
/// <see cref="Zone"/> links a platform to the navigation zone available at <see cref="Start"/>.
/// </summary>
public struct PatrolRail : IComponent {
	public FPos Start;
	public FPos End;
	public Fixed32.FP Speed;
	public bool MovingToEnd;
	public int DwellTicksRemaining;
	public int Zone;
}
