using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public class PhysicsRes : IResource {
	public FP Gravity = 24.ToFP();
}
