using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using static Space.GameCore.Core<Space.GameCore.ServerWorld>;

namespace Space.GameCore;

public struct ServerWorld : IWorldType, ISessionType { }

public abstract class SRVR : Server<ServerWorld> { }

public static class ServerSetup {
	public static void CreateAndInitialize(IRemoteClientListener remoteClientListener, LevelFile level, ILogger? logger = null) {
		try {
			SRVR.Create(GameSessionSetup.SessionConfig, remoteClientListener, new GameWorldFullSyncHandler(), logger: logger);
			GameSessionSetup.Register();
			SRVR.Initialize();

			GameWorldSetup.CreateAndInitialize(level.Data);
		} catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
			Destroy();
			throw;
		}
	}

	public static void Destroy() {
		if (W.Status != WorldStatus.NotCreated) {
			GameWorldSetup.Destroy();
		}
		if (SRVR.IsCreated) {
			SRVR.Destroy();
		}
	}
}
