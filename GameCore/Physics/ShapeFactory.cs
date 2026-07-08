using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class ShapeFactory {
		/// <summary>
		/// Creates a shape entity owned by <paramref name="body"/>. Mirrors box3d's b3CreateShape:
		/// computes the local centroid/margin and, if requested, recomputes the body's mass. Does not
		/// create a broad-phase proxy — that happens in <c>ShapeProxySystem</c> once a <c>BroadPhase</c>
		/// resource exists, so this factory has no dependency on it.
		/// </summary>
		public static W.Entity CreateShape(W.Entity body, Shape shapeData) {
			var shapeEntity = W.NewEntity<Default>();

			shapeData.LocalCentroid = shapeData.ComputeCentroid();
			shapeData.AabbMargin = shapeData.ComputeMargin();
			shapeEntity.Set(shapeData);

			// Populates body's Links<Shapes> automatically via BodyOwner.OnAdd.
			shapeEntity.Set(new W.Link<BodyOwner>(body));

			if (shapeData.UpdateBodyMass) {
				BodyMassUpdate.Update(body);
			}

			return shapeEntity;
		}

		/// <summary>
		/// Destroys a shape entity and its broad-phase proxy. Explicit rather than tag-driven: a
		/// dead entity is invisible to every query (including <c>AllDeleted&lt;T&gt;</c>, since the
		/// alive mask filters it out — see static-ecs's tracking docs), so a scan-for-destroyed-shapes
		/// system pass can't work here the way the old grid broadphase's did. Callers that need to
		/// react to shape removal first (contacts, mass updates) should do so before calling this.
		/// </summary>
		public static void DestroyShape(W.Entity shapeEntity, BroadPhase broadPhase) {
			ref var shape = ref shapeEntity.Ref<Shape>()!; // shapeEntity is always created by CreateShape, which sets Shape.
			ShapeBroadPhaseOps.DestroyProxy(ref shape, broadPhase);

			var updateBodyMass = shape.UpdateBodyMass;
			var hasOwner = shapeEntity.Has<W.Link<BodyOwner>>();
			var body = hasOwner ? shapeEntity.Ref<W.Link<BodyOwner>>()!.Value : default;

			shapeEntity.Destroy();

			if (updateBodyMass && hasOwner && body.TryUnpack<TWorld>(out var bodyEntity)) {
				BodyMassUpdate.Update(bodyEntity);
			}
		}
	}
}
