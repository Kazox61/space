using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// What a <see cref="Mover"/>-driven character wants to do this tick, written by whichever system
/// controls it (<c>PlayerIntentSystem</c> from input, <c>NavAgentSystem</c> from its path) and
/// executed by <c>CharacterMoverSystem</c>. Rewritten every tick before the mover runs.
/// </summary>
public struct CharacterMoveIntent : IComponent {
	/// <summary>Desired horizontal velocity in world units per second: X is world X, Y is world Z.</summary>
	public FVector2 Velocity;

	/// <summary>Jump this tick if grounded. Edge-triggered: the controller sets it for one tick only.</summary>
	public bool Jump;
}
