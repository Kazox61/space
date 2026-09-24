using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Where systems report one-shot effects. Static per closed <c>Core&lt;TWorld&gt;</c>, so the
	/// server's world keeps it null and never records anything; the client sets it in
	/// <c>ClientSetup</c>. Lives outside the ECS world on purpose: nothing is snapshotted or rolled
	/// back, so a re-simulated tick reports its effects again and the sink deduplicates them.
	/// </summary>
	public static IFxSink? FxSink;

	/// <summary>Reports <paramref name="fx"/> for the tick being simulated (inside a system, <c>S.CurrentTick</c> is that tick).</summary>
	public static void RecordFx(in FxEvent fx) {
		FxSink?.Record(fx, S.CurrentTick);
	}
}
