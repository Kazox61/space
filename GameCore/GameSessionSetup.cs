using Shenanicode.Rollback;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class GameSessionSetup {
		public static SessionConfig SessionConfig => new(tickRate: 60);

		public static void Register() {
			S.SetUpdateRoot(new GameUpdateRoot());
			S.SetRollback(new GameWorldRollback(S.FramesCapacity));
			S.Types().RegisterAll(typeof(CoreRoot).Assembly);
		}
	}
}
