using System;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>Safe mutation boundary for live shape geometry, density, and collision filters.</summary>
	public static class ShapeOperations {
		public static void SetFilter(W.Entity shapeEntity, Filter filter) {
			var bodyEntity = RequireOwner(shapeEntity);
			ref readonly var currentShape = ref RequireShape(shapeEntity);
			if (currentShape.Filter.CategoryBits == filter.CategoryBits
				&& currentShape.Filter.MaskBits == filter.MaskBits
				&& currentShape.Filter.GroupIndex == filter.GroupIndex) {
				return;
			}
			var broadPhase = W.GetResource<BroadPhase>();
			InvalidateShape(shapeEntity, broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Filter = filter;
			Wake(bodyEntity);
			RecreateProxyIfEnabled(shapeEntity, bodyEntity, ref shape, broadPhase);
			broadPhase.UpdatePairs(ContactSystem.TryCreateContact);
		}

		public static void SetDensity(W.Entity shapeEntity, FP density) {
			if (density < FP.Zero) {
				throw new ArgumentOutOfRangeException(nameof(density), "Shape density cannot be negative.");
			}
			var bodyEntity = RequireOwner(shapeEntity);
			RequireShape(shapeEntity).Density = density;
			BodyMassUpdate.Update(bodyEntity);
			Wake(bodyEntity);
		}

		public static void SetSphere(W.Entity shapeEntity, Sphere sphere) {
			var bodyEntity = PrepareGeometryChange(shapeEntity, out var broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Type = ShapeType.Sphere;
			shape.SphereShape = sphere;
			FinishGeometryChange(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		public static void SetCapsule(W.Entity shapeEntity, Capsule capsule) {
			var bodyEntity = PrepareGeometryChange(shapeEntity, out var broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Type = ShapeType.Capsule;
			shape.CapsuleShape = capsule;
			FinishGeometryChange(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		public static void SetHull(W.Entity shapeEntity, Hull hull) {
			var bodyEntity = PrepareGeometryChange(shapeEntity, out var broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Type = ShapeType.Hull;
			shape.HullShape = hull;
			FinishGeometryChange(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		public static void SetBox(W.Entity shapeEntity, FVector3 center, FVector3 halfExtents) =>
			SetHull(shapeEntity, Hull.MakeBox(halfExtents, center));

		public static void SetBox(W.Entity shapeEntity, FVector3 center, FVector3 halfExtents, FQuaternion rotation) =>
			SetHull(shapeEntity, Hull.MakeBox(halfExtents, center, rotation));

		private static W.Entity PrepareGeometryChange(W.Entity shapeEntity, out BroadPhase broadPhase) {
			var bodyEntity = RequireOwner(shapeEntity);
			broadPhase = W.GetResource<BroadPhase>();
			InvalidateShape(shapeEntity, broadPhase);
			return bodyEntity;
		}

		private static void FinishGeometryChange(W.Entity shapeEntity, W.Entity bodyEntity, ref Shape shape, BroadPhase broadPhase) {
			shape.LocalCentroid = shape.ComputeCentroid();
			shape.AabbMargin = shape.ComputeMargin();
			BodyMassUpdate.Update(bodyEntity);
			Wake(bodyEntity);
			RecreateProxyIfEnabled(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		private static void InvalidateShape(W.Entity shapeEntity, BroadPhase broadPhase) {
			ContactLifecycle.DestroyContactsForShape(shapeEntity.GID, broadPhase);
			broadPhase.ForgetPairsForShape(shapeEntity.GID);
			ref var shape = ref RequireShape(shapeEntity);
			ShapeBroadPhaseOps.DestroyProxy(ref shape, broadPhase);
		}

		private static void RecreateProxyIfEnabled(W.Entity shapeEntity, W.Entity bodyEntity, ref Shape shape, BroadPhase broadPhase) {
			ref readonly var body = ref RequireBody(bodyEntity);
			if (BodyOperations.IsEnabled(body)) {
				ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, broadPhase, body.Type, body.Transform, true);
			}
		}

		private static W.Entity RequireOwner(W.Entity shapeEntity) {
			if (!shapeEntity.Has<Shape>() || !shapeEntity.Has<W.Link<BodyOwner>>()) {
				throw new InvalidOperationException("Shape operation requires a shape with a body owner.");
			}
			ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
			if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity) || !bodyEntity.Has<Body>()) {
				throw new InvalidOperationException("Shape operation requires a live body owner.");
			}
			return bodyEntity;
		}

		private static ref Shape RequireShape(W.Entity shapeEntity) {
			if (!shapeEntity.Has<Shape>()) {
				throw new InvalidOperationException("Shape operation requires an entity with Shape.");
			}
			return ref shapeEntity.Ref<Shape>();
		}

		private static ref Body RequireBody(W.Entity bodyEntity) {
			if (!bodyEntity.Has<Body>()) {
				throw new InvalidOperationException("Shape operation requires a live body owner.");
			}
			return ref bodyEntity.Ref<Body>();
		}

		private static void Wake(W.Entity bodyEntity) {
			ref var body = ref RequireBody(bodyEntity);
			if (body.Type != BodyType.Static) {
				body.IsAwake = true;
			}
		}
	}
}
