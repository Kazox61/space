using Shenanicode.Rollback;
using FFS.Libraries.StaticEcs;


namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public class GameUpdateRoot : IUpdateRoot {
		public void Update(int tick) {
			Systems.Update();
		}
	}
}
