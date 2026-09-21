using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Snapshot of physics object counts for lifecycle regression checks and leak diagnostics
	/// (Phase 0 of the Box3D migration plan -- see docs/physics-box3d-migration-assessment.md).
	/// </summary>
	public readonly record struct PhysicsCounts(int Bodies, int Shapes, int Proxies, int Contacts, int CachedPairs, int MovedProxies) {
		public override string ToString() => $"bodies={Bodies} shapes={Shapes} proxies={Proxies} contacts={Contacts} pairs={CachedPairs} moved={MovedProxies}";
	}

	public static class PhysicsDiagnostics {
		/// <summary>
		/// Captures the current physics object counts. Call after <c>Systems.Update()</c>: shapes only
		/// receive their broad-phase proxies during <see cref="ShapeProxySystem"/>'s update, so a
		/// capture taken between shape creation and the next update undercounts proxies.
		/// </summary>
		public static PhysicsCounts Capture() {
			var broadPhase = W.GetResource<BroadPhase>();
			return new PhysicsCounts(
				W.Query<All<Body>>().EntitiesCount(),
				W.Query<All<Shape>>().EntitiesCount(),
				broadPhase.ProxyCount,
				W.Query<All<Contact>>().EntitiesCount(),
				broadPhase.CachedPairCount,
				broadPhase.MovedProxyCount
			);
		}
	}
}
