using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Mirrors every physics-driven entity's <see cref="Body.Transform"/> into its gameplay-facing
	/// <see cref="Transform"/> component, once per tick right after <see cref="ContactSolverSystem"/>
	/// finalizes body positions. Apart from <see cref="BodyOperations"/>, this is the only code allowed
	/// to write <see cref="Transform"/> on an entity that also has a <see cref="Body"/> -- gameplay logic and view
	/// behaviors) treats <see cref="Transform"/> as read-only for such entities and drives movement
	/// through <see cref="BodyOperations"/> instead.
	/// </summary>
	public struct BodyTransformSyncSystem : ISystem {
		public void Update() {
			W.Query().For(static (ref Body body, ref Transform transform) => {
				transform.SetFromWorldTransform(body.Transform);
			});
		}
	}
}
