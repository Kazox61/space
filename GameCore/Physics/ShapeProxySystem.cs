using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Keeps broad-phase proxies in sync with shapes: creates a proxy for every shape that doesn't
	/// have one yet, and refreshes every non-static shape's AABB each tick. Explicit shape
	/// destruction (and its proxy cleanup) is handled by <see cref="ShapeFactory"/> directly — see
	/// its remarks for why this isn't a tag-scanning pass like the old grid broadphase's.
	///
	/// Deliberately uses value checks (ProxyKey == NullProxyKey; body.Type != Static) instead of
	/// tick-based tracking filters (AllAdded/AllChanged). Per-system tracking bookkeeping
	/// (Systems&lt;T&gt;'s internal per-system LastTick) is not part of any snapshot mechanism in this
	/// ECS — WorldSnapshot restores the global tick counter but not each system's own last-seen-tick,
	/// so after a rollback a tracking-filter system computes a nonsensical (LastTick > CurrentTick)
	/// range and throws a tracking-buffer-overflow assertion. Since this project's whole point is
	/// rollback netcode, tracking filters are a non-starter here regardless of their appeal as a
	/// micro-optimization — recomputing every non-static shape's AABB every tick is exactly what
	/// box3d itself does anyway (UpdateAABBs only actually moves the broad-phase proxy when the tight
	/// AABB outgrows the fat one, so the steady-state cost is just a transform + min/max per shape).
	/// </summary>
	public struct ShapeProxySystem : ISystem {
		public void Update() {
			var broadPhase = W.GetResource<BroadPhase>();

			W.Query<All<W.Link<BodyOwner>>>().For(ref broadPhase,
				static (ref BroadPhase bp, W.Entity shapeEntity, ref Shape shape) => {
					ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
					if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
						return;
					}

					ref readonly var body = ref bodyEntity.Read<Body>()!; // BodyOwner always links to an entity with Body.

					if (shape.ProxyKey == Shape.NullProxyKey) {
						var forcePairCreation = body.Type != BodyType.Static || shape.InvokeContactCreation;
						ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, bp, body.Type, body.Transform, forcePairCreation);
						return;
					}

					if (body.Type != BodyType.Static) {
						ShapeBroadPhaseOps.UpdateAABBs(ref shape, body.Transform, bp);
					}
				});
		}
	}
}
