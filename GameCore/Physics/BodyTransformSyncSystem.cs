using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Mirrors every physics-driven entity's <see cref="Body.Transform"/> into its gameplay-facing
	/// <see cref="Transform"/> component, once per tick right after <see cref="ContactSolverSystem"/>
	/// finalizes body positions. This is the only system allowed to write <see cref="Transform"/> on
	/// an entity that also has a <see cref="Body"/> -- everything else (gameplay logic, view
	/// behaviors) treats <see cref="Transform"/> as read-only for such entities and drives movement
	/// through <see cref="Body"/> instead (velocity for kinematic/dynamic bodies, or
	/// <see cref="Body.Transform"/> directly for teleports/static placement).
	/// </summary>
	public struct BodyTransformSyncSystem : ISystem {
		public void Update() {
			W.Query().For(static (ref Body body, ref Transform transform) => {
				transform.SetFromWorldTransform(body.Transform);
			});
		}
	}
}
