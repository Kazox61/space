using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public struct JumpState : IComponent {
	public FP VerticalVelocity;
	public bool Grounded;
}
