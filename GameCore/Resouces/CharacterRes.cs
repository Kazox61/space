using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public class CharacterRes : IResource {
	public FP JumpForce = 8.ToFP();
	public FP MoveSpeed = 7.ToFP();
}
