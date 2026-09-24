using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public class GameWorldFullSyncHandler : IFullSyncHandler {
		public void WriteFullSync(ref BinaryPackWriter writer) {
			W.Serializer.CreateWorldSnapshot(ref writer);
		}

		public void ReadFullSync(ref BinaryPackReader reader) {
			W.Serializer.LoadWorldSnapshot(ref reader, hardReset: true);
			// A full sync starts a new timeline; keys recorded on the old one mean nothing on it.
			FxSink?.Clear();
		}
	}
}
