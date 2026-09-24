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
			var categoryChanged = currentShape.Filter.CategoryBits != filter.CategoryBits;
			ref var shape = ref RequireShape(shapeEntity);
			shape.Filter = filter;
			ContactLifecycle.ReevaluateContactsForShape(shapeEntity, broadPhase);
			Wake(bodyEntity);
			if (categoryChanged) {
				ShapeBroadPhaseOps.DestroyProxy(ref shape, broadPhase);
				RecreateProxyIfEnabled(shapeEntity, bodyEntity, ref shape, broadPhase);
			} else if (shape.ProxyKey != Shape.NullProxyKey) {
				broadPhase.MoveProxy(shape.ProxyKey, shape.FatAabb);
			}
			broadPhase.UpdatePairs(ContactSystem.TryCreateContact);
		}

		public static void SetEventFlags(W.Entity shapeEntity, bool contactEvents, bool sensorEvents, bool hitEvents) {
			var bodyEntity = RequireOwner(shapeEntity);
			ref readonly var currentShape = ref RequireShape(shapeEntity);
			if (currentShape.EnableContactEvents == contactEvents
				&& currentShape.EnableSensorEvents == sensorEvents
				&& currentShape.EnableHitEvents == hitEvents) {
				return;
			}
			var broadPhase = W.GetResource<BroadPhase>();
			ref var shape = ref RequireShape(shapeEntity);
			shape.EnableContactEvents = contactEvents;
			shape.EnableSensorEvents = sensorEvents;
			shape.EnableHitEvents = hitEvents;
			ContactLifecycle.ReevaluateEventFlagsForShape(shapeEntity, broadPhase);
			Wake(bodyEntity);
			if (shape.ProxyKey != Shape.NullProxyKey) {
				broadPhase.MoveProxy(shape.ProxyKey, shape.FatAabb);
			}
			broadPhase.UpdatePairs(ContactSystem.TryCreateContact);
		}

		public static void SetDensity(W.Entity shapeEntity, FP density) {
			var bodyEntity = RequireOwner(shapeEntity);
			var candidate = RequireShape(shapeEntity);
			candidate.Density = density;
			ValidateCandidate(candidate, bodyEntity, nameof(density));
			ValidateBodyMassChange(shapeEntity, bodyEntity, candidate);
			RequireShape(shapeEntity).Density = density;
			BodyMassUpdate.Update(bodyEntity);
			Wake(bodyEntity);
		}

		public static void SetSphere(W.Entity shapeEntity, Sphere sphere) {
			var owner = RequireOwner(shapeEntity);
			var candidate = RequireShape(shapeEntity);
			candidate.Type = ShapeType.Sphere;
			candidate.SphereShape = sphere;
			ValidateCandidate(candidate, owner, nameof(sphere));
			ValidateBodyMassChange(shapeEntity, owner, candidate);
			var bodyEntity = PrepareGeometryChange(shapeEntity, out var broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Type = ShapeType.Sphere;
			shape.SphereShape = sphere;
			FinishGeometryChange(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		public static void SetCapsule(W.Entity shapeEntity, Capsule capsule) {
			var owner = RequireOwner(shapeEntity);
			var candidate = RequireShape(shapeEntity);
			candidate.Type = ShapeType.Capsule;
			candidate.CapsuleShape = capsule;
			ValidateCandidate(candidate, owner, nameof(capsule));
			ValidateBodyMassChange(shapeEntity, owner, candidate);
			var bodyEntity = PrepareGeometryChange(shapeEntity, out var broadPhase);
			ref var shape = ref RequireShape(shapeEntity);
			shape.Type = ShapeType.Capsule;
			shape.CapsuleShape = capsule;
			FinishGeometryChange(shapeEntity, bodyEntity, ref shape, broadPhase);
		}

		public static void SetHull(W.Entity shapeEntity, Hull hull) {
			hull.Rotation = FQuaternion.Normalize(hull.Rotation);
			var owner = RequireOwner(shapeEntity);
			var candidate = RequireShape(shapeEntity);
			candidate.Type = ShapeType.Hull;
			candidate.HullShape = hull;
			ValidateCandidate(candidate, owner, nameof(hull));
			ValidateBodyMassChange(shapeEntity, owner, candidate);
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

		public static void SetMaterial(W.Entity shapeEntity, SurfaceMaterial material) {
			var bodyEntity = RequireOwner(shapeEntity);
			var candidate = RequireShape(shapeEntity);
			candidate.Material = material;
			ValidateCandidate(candidate, bodyEntity, nameof(material));
			RequireShape(shapeEntity).Material = material;
			Wake(bodyEntity);
		}

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

		private static void ValidateCandidate(in Shape candidate, W.Entity bodyEntity, string parameterName) {
			ref readonly var body = ref RequireBody(bodyEntity);
			PhysicsValidation.ValidateShape(candidate, body.Type, body.Transform, parameterName);
		}

		// Trial-runs the mass update with the candidate in place, then restores both structs verbatim.
		// Re-running the update to restore would re-apply its center-shift velocity correction, which
		// does not round-trip exactly in fixed point.
		private static void ValidateBodyMassChange(W.Entity shapeEntity, W.Entity bodyEntity, in Shape candidate) {
			ref var liveShape = ref RequireShape(shapeEntity);
			ref var liveBody = ref RequireBody(bodyEntity);
			var originalShape = liveShape;
			var originalBody = liveBody;
			try {
				liveShape = candidate;
				BodyMassUpdate.Update(bodyEntity);
			} finally {
				liveShape = originalShape;
				liveBody = originalBody;
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

		private static void Wake(W.Entity bodyEntity) => PhysicsSleep.WakeBody(ref RequireBody(bodyEntity));
	}
}
