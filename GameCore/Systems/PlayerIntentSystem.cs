using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Turns each player's input into their <see cref="CharacterMoveIntent"/>: the 2D move stick
	/// maps to world XZ at <see cref="CharacterRes.MoveSpeed"/>, and a fresh jump press becomes a
	/// one-tick jump request. <see cref="CharacterMoverSystem"/> executes it.
	/// </summary>
	public struct PlayerIntentSystem : ISystem {
		public void Update() {
			var moveSpeed = Systems.GetResource<CharacterRes>().MoveSpeed;
			W.Query().For(moveSpeed, static (ref Fixed64.FP moveSpeed, ref PlayerInfo playerInfo, ref CharacterMoveIntent intent) => {
				var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel);
				var lastInput = input.LastFresh();
				var moveInput = Fixed64.FVector2.NormalizeSafe(new Fixed64.FVector2(lastInput.MoveX, lastInput.MoveY));

				intent.Velocity = moveInput * moveSpeed;
				// Jump is edge-triggered: a predicted tick carries the previous input forward (Aged), so
				// only a fresh input may jump, or one press would re-jump on every predicted landing.
				intent.Jump = input.IsFresh && lastInput.Jump;
			});
		}
	}
}
