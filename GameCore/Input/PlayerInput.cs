using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public struct PlayerInput : IInput {
	public FP MoveX;
	public FP MoveY;
	public FP AttackX;
	public FP AttackY;
	public bool Jump;
}
