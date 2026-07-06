using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public class PhysicsWorld : IResource {
		public FVector3 Gravity = new(FP.Zero, -10 * B3Config.GetLengthUnitsPerMeter(), FP.Zero);

		public FP ContactHertz = FP.FromRatio(30, 1);
		public FP ContactDampingRatio = FP.FromRatio(10, 1);
		public FP ContactSpeed = 3 * B3Config.GetLengthUnitsPerMeter();
		public FP RestitutionThreshold = B3Config.GetLengthUnitsPerMeter();
		public FP MaximumLinearSpeed = 400 * B3Config.GetLengthUnitsPerMeter();
		public bool EnableWarmStarting = true;

		/// <summary>Number of solver sub-steps per full step. Box3d's usual default is 4.</summary>
		public int SubStepCount = 4;
	}
}
