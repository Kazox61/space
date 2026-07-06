using FFS.Libraries.StaticPack;
using Space.GameCore;
using Shenanicode.Rollback;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public class GameInterpolationReceiver : IInterpolationReceiver {
	private BinaryPackWriter _buffer = BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);

	public void SaveInterpolationState() {
		_buffer.Position = 0;
		W.Serializer.CreateWorldSnapshot(ref _buffer);
		var reader = _buffer.AsReader();
		WP.Serializer.LoadWorldSnapshot(ref reader, true);
	}
}
