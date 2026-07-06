using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct SpawnPlayerSystem : ISystem {
		public void Update() {
			foreach (var (channel, data) in S.GetAllSignals<PlayerConnectedSignal>()) {
				W.NewEntity(new Player { PlayerGuid = data.PlayerGuid, InputChannel = channel });
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
