using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Writes every <see cref="NavZoneState"/> into the world's navmesh before agents plan. The mesh's
	/// zone triangles are derived state: they are rebuilt from the snapshotted zone entities every
	/// tick, never updated only on change, so a rollback or full sync cannot leave stale flags behind.
	/// A zone without an entity is open at its baked cost.
	/// </summary>
	public struct NavZoneApplySystem : ISystem {
		public void Update() {
			var navigation = Systems.GetResource<NavigationRes>();
			if (navigation.Zones.Count == 0) {
				return;
			}
			navigation.ResetZones();
			foreach (var entity in W.Query<All<NavZoneState>>().Entities()) {
				navigation.AddZoneState(entity.Read<NavZoneState>());
			}
			navigation.ApplyZones();
		}
	}
}
