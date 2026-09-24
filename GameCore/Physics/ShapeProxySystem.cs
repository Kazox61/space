using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
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
		internal static void RefreshBodyAABBs(W.Entity bodyEntity, in Body body, BroadPhase broadPhase) {
			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				return;
			}

			ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
			for (var i = 0; i < links.Length; i++) {
				if (links[i].Value.TryUnpack<TWorld>(out var shapeEntity) && shapeEntity.Has<Shape>()) {
					ShapeBroadPhaseOps.UpdateAABBs(ref shapeEntity.Ref<Shape>(), body.Transform, broadPhase);
				}
			}
		}

		public void Update() {
			var runtime = PhysicsRuntime.Get();
			var timestamp = PhysicsRuntime.Timestamp();
			var broadPhase = W.GetResource<BroadPhase>();
			var dt = Const.DeltaTime.To32();

			foreach (var shapeEntity in W.Query<All<Shape, W.Link<BodyOwner>>>().Entities()) {
				ref var shape = ref shapeEntity.Ref<Shape>();
				ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
				if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
					continue;
				}

				ref readonly var body = ref bodyEntity.Read<Body>()!; // BodyOwner always links to an entity with Body.
				if (!BodyOperations.IsEnabled(body)) {
					if (shape.ProxyKey != Shape.NullProxyKey) {
						ShapeBroadPhaseOps.DestroyProxy(ref shape, broadPhase);
					}
					continue;
				}

				if (shape.ProxyKey == Shape.NullProxyKey) {
					var forcePairCreation = body.Type != BodyType.Static || shape.InvokeContactCreation;
					ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, broadPhase, body.Type, body.Transform, forcePairCreation);
				}

				// Sleeping bodies do not move, so their proxies stay valid until they wake.
				if (PhysicsSleep.IsSimulated(body)) {
					var predictedTranslation = dt * body.LinearVelocity;
					var predictedRotation = FQuaternion.IntegrateRotation(FQuaternion.Identity, dt * body.AngularVelocity);
					var angularMotion = FQuaternion.GetAngle(predictedRotation) * shape.ComputeSweepRadius(body.LocalCenter);
					var fast = body.IsBullet || (body.Type == BodyType.Dynamic
						&& FVector3.Length(predictedTranslation) + angularMotion > FP.Half * shape.ComputeMinimumExtent());
					if (fast) {
						ShapeBroadPhaseOps.UpdateSweptAABBs(ref shape, body, predictedTranslation, broadPhase);
					} else {
						ShapeBroadPhaseOps.UpdateAABBs(ref shape, body.Transform, broadPhase);
					}
				}
			}
			runtime.AddTime(PhysicsPhase.ProxyUpdate, timestamp);
		}
	}
}
