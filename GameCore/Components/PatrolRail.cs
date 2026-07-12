using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Marks a kinematic <see cref="Body"/> as sliding back and forth along world X between
/// <see cref="Min"/> and <see cref="Max"/> -- see <see cref="Core{TWorld}.DummyPatrolSystem"/>, which
/// flips <see cref="Body.LinearVelocity"/>'s sign once the body reaches either end.
/// </summary>
public struct PatrolRail : IComponent {
	public FP Min;
	public FP Max;
}
