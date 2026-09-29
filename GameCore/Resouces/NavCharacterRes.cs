using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>Tuning for the <see cref="NavCharacter"/> spawned by <c>Core&lt;TWorld&gt;.SpawnNavCharacterSystem</c>.</summary>
public class NavCharacterRes : IResource {
	/// <summary>Capsule center at spawn. The default stands on the sample ground (top at y=0.5), behind its test box as seen from the player spawn.</summary>
	public FVector3 SpawnPosition = new(FP.Zero, FP.One + FP.Half, FP.FromRatio(-11, 1));

	public FP MoveSpeed = 4.ToFP();
	public FP ArrivalRadius = FP.FromRatio(3, 2);
	public int RepathIntervalTicks = 30;

	/// <summary>How far the chased player must move from the current destination before it is updated.</summary>
	public FP RetargetDistance = FP.One;
}
