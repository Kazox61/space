using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Keeps broad-phase proxies in sync with shapes: creates a proxy for every newly added shape,
	/// and refreshes shape AABBs/proxies whenever their owning body's transform changes. Explicit
	/// shape destruction (and its proxy cleanup) is handled by <see cref="ShapeFactory"/> directly —
	/// see its remarks for why this isn't a tag-scanning pass like the old grid broadphase's.
	/// </summary>
	public struct ShapeProxySystem : ISystem {
		public void Update() {
			var broadPhase = Systems.GetResource<BroadPhase>();

			// Entity-only delegates + explicit Ref/Read below, rather than component delegate
			// params: mixing `ref`/`in` component params with the ref-data-entity overload family
			// is ambiguous here, and pass 2 must not mark Body as Changed (it would perpetually
			// re-match its own AllChanged<Body> filter).
			W.Query<AllAdded<Shape>, All<Shape, W.Link<BodyOwner>>>().For(ref broadPhase,
				static (ref BroadPhase bp, W.Entity shapeEntity) => {
					ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
					if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
						return;
					}

					ref var shape = ref shapeEntity.Ref<Shape>();
					ref readonly var body = ref bodyEntity.Read<Body>();
					var forcePairCreation = body.Type != BodyType.Static || shape.InvokeContactCreation;
					ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, bp, body.Type, body.Transform, forcePairCreation);
				});

			W.Query<AllChanged<Body>>().For(ref broadPhase,
				static (ref BroadPhase bp, W.Entity bodyEntity) => {
					if (!bodyEntity.Has<W.Links<Shapes>>()) {
						return;
					}

					ref readonly var body = ref bodyEntity.Read<Body>();
					ref var shapeLinks = ref bodyEntity.Ref<W.Links<Shapes>>();
					for (var i = 0; i < shapeLinks.Length; i++) {
						if (shapeLinks[i].Value.TryUnpack<TWorld>(out var shapeEntity)) {
							ref var shape = ref shapeEntity.Ref<Shape>();
							ShapeBroadPhaseOps.UpdateAABBs(ref shape, body.Transform, bp);
						}
					}
				});
		}
	}
}
