using Game.Client;
using Space.GameCore;
using Shenanicode.Rollback;
using FFS.Libraries.StaticEcs;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public struct ClientWorld : IWorldType, ISessionType { }

public abstract class CLNT : Client<ClientWorld> { }

public static class ClientSetup {
	public static void CreateAndInitialize(ServerConnection connection) {
		CLNT.Create(GameSessionSetup.SessionConfig,
			connection,
			new GameWorldFullSyncHandler(),
			logger: new GodotLogger("Client"),
			tickSyncConfig: TickSyncConfig.Robust);
		GameSessionSetup.Register();
		CLNT.Initialize();

		GameWorldSetup.CreateAndInitialize();
		GameInterpolationSetup.CreateAndInitialize();
	}

	public static void Destroy() {
		if (CLNT.Status != SessionStatus.NotCreated) {
			GameInterpolationSetup.Destroy();
			GameWorldSetup.Destroy();
			CLNT.Destroy();
		}
	}
}
