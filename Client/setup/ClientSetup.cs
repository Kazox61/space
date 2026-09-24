using FFS.Libraries.StaticEcs;
using Game.Client;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public struct ClientWorld : IWorldType, ISessionType { }

public abstract class CLNT : Client<ClientWorld> { }

public static class ClientSetup {
	/// <summary>Collects the simulation's one-shot effects; <see cref="ClientGame"/> flushes it after every simulation update.</summary>
	public static FxLog FxLog { get; private set; }

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

		FxLog = new FxLog(S.RollbackTicksCapacity);
		FxSink = FxLog;
	}

	public static void Destroy() {
		FxSink = null;
		FxLog = null;
		if (CLNT.Status != SessionStatus.NotCreated) {
			GameInterpolationSetup.Destroy();
			GameWorldSetup.Destroy();
			CLNT.Destroy();
		}
	}
}
