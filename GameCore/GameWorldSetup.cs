using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class GameWorldSetup {
		public static WorldConfig WorldConfig => new() {
			TrackingBufferSize = 2,
			TrackCreated = true
		};

		/// <param name="registerSystems">
		/// Adds systems after the simulation's own and before initialization, for tests that drive
		/// world state from inside the tick.
		/// </param>
		public static void CreateAndInitialize(LevelData level, Action? registerSystems = null) {
			W.Create(WorldConfig);
			Systems.Create(snapshotGuid: GameSystemsSnapshotGuid);

			GameTypes.Register<TWorld>();
			SimulationSetup.Register();
			registerSystems?.Invoke();
			Systems.SetResource(new NavigationRes(level.Navigation, NavConfig.Default));

			W.Initialize();
			Systems.Initialize();

			LevelLoader.Load(level);
		}

		public static void Destroy() {
			Systems.Destroy();
			W.Destroy();
		}
	}
}
