using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct SpawnPlayerSystem : ISystem {
		public void Update() {
			foreach (var (channel, data) in S.GetAllSignals<PlayerConnectedSignal>()) {
				var player = W.NewEntity(new Player { PlayerGuid = data.PlayerGuid, InputChannel = channel });

				// Standing capsule: two half-radius hemispheres a body-length apart, feet at the body origin.
				var capsule = Shape.MakeCapsule(new FVector3(FP.Zero, FP.Half, FP.Zero), new FVector3(FP.Zero, FP.One + FP.Half, FP.Zero), FP.Half);
				ShapeFactory.CreateShape(player, capsule);
			}

			foreach (var (channel, data) in S.GetAllSignals<PlayerDisconnectedSignal>()) {
				W.Query().For(channel, static (ref int channel, W.Entity entity, ref PlayerInfo playerInfo) => {
					if (playerInfo.InputChannel == channel) {
						entity.Destroy();
					}
				});
			}
		}
	}
}
