using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class GameWorldSetup {
		public static WorldConfig WorldConfig => new() {
			TrackingBufferSize = 2,
			TrackCreated = true
		};

		public static void CreateAndInitialize() {
			W.Create(WorldConfig);
			Systems.Create();

			W.Types().RegisterAll(typeof(CoreRoot).Assembly);
			SimulationSetup.Register();

			W.Initialize();
			Systems.Initialize();
		}

		public static void Destroy() {
			Systems.Destroy();
			W.Destroy();
		}
	}
}
