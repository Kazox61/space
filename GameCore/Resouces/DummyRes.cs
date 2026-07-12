using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>Tuning for the row of patrolling <see cref="Dummy"/> targets spawned by <c>Core&lt;TWorld&gt;.SpawnDummySystem</c>.</summary>
public class DummyRes : IResource {
	public int Count = 4;
	public FP RailMin = FP.FromRatio(-3, 1);
	public FP RailMax = FP.FromRatio(3, 1);
	public FP Speed = 2.ToFP();

	/// <summary>World Z of the first dummy; each subsequent one sits <see cref="RowSpacing"/> further away.</summary>
	public FP RowZ = FP.FromRatio(-14, 1);
	public FP RowSpacing = 3.ToFP();

	/// <summary>Seconds after a Dummy dies before <c>Core&lt;TWorld&gt;.DummyRespawnSystem</c> recreates it at the same rail slot.</summary>
	public FP RespawnDelay = 5.ToFP();
}
