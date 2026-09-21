using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>Safe mutation boundary for body state that has physics-derived data.</summary>
	public static class BodyOperations {
		public static bool IsEnabled(in Body body) => !body.EnableStateInitialized || body.IsEnabled;

		public static void CreateBody(W.Entity entity, BodyType type, FWorldTransform transform) {
			if (entity.Has<Body>()) {
				throw new InvalidOperationException("BodyOperations.CreateBody cannot overwrite an existing body.");
			}
			if (entity.Has<W.Links<Shapes>>() && entity.Read<W.Links<Shapes>>().Length > 0) {
				throw new InvalidOperationException("A body must be created before its shapes.");
			}

			transform.Rotation = FQuaternion.Normalize(transform.Rotation);
			entity.Set(new Body {
				Type = type,
				Transform = transform,
				Center = transform.Position,
				GravityScale = FP.One,
				EnableSleep = true,
				IsAwake = true,
				IsEnabled = true,
				EnableStateInitialized = true,
				EnableContactRecycling = true,
				DeltaRotation = FQuaternion.Identity,
			});

			SyncGameplayTransform(entity, transform);
		}

		public static void DestroyBody(W.Entity entity) => PhysicsBodyLifecycle.DestroyBody(entity);

		public static void SetTransform(W.Entity entity, FWorldTransform transform) {
			ref var body = ref RequireBody(entity);
			transform.Rotation = FQuaternion.Normalize(transform.Rotation);
			body.Transform = transform;
			body.Center = FWorldTransform.TransformPoint(transform, body.LocalCenter);
			body.DeltaPosition = FVector3.Zero;
			body.DeltaRotation = FQuaternion.Identity;
			UpdateWorldInertia(ref body);
			Wake(ref body);
			SyncGameplayTransform(entity, transform);
			var enabled = IsEnabled(body);
			var bodyType = body.Type;
			var bodyTransform = body.Transform;

			var broadPhase = W.GetResource<BroadPhase>();
			InvalidateBodyProxies(entity, broadPhase);
			ForEachOwnedShape(entity, shapeEntity => {
				ref var shape = ref shapeEntity.Ref<Shape>();
				if (enabled) {
					ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, broadPhase, bodyType, bodyTransform, true);
				}
			});
		}

		public static void SetType(W.Entity entity, BodyType type) {
			ref var body = ref RequireBody(entity);
			if (body.Type == type) {
				return;
			}

			var broadPhase = W.GetResource<BroadPhase>();
			InvalidateBodyProxies(entity, broadPhase);

			body.Type = type;
			body.Force = FVector3.Zero;
			body.Torque = FVector3.Zero;
			if (type == BodyType.Static) {
				body.LinearVelocity = FVector3.Zero;
				body.AngularVelocity = FVector3.Zero;
			}
			BodyMassUpdate.Update(entity);
			body.DeltaPosition = FVector3.Zero;
			body.DeltaRotation = FQuaternion.Identity;
			Wake(ref body);
			var enabled = IsEnabled(body);
			var bodyTransform = body.Transform;

			if (enabled) {
				ForEachOwnedShape(entity, shapeEntity => {
					ref var shape = ref shapeEntity.Ref<Shape>();
					ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, broadPhase, type, bodyTransform, true);
				});
			}
		}

		public static void SetEnabled(W.Entity entity, bool enabled) {
			ref var body = ref RequireBody(entity);
			if (body.EnableStateInitialized && body.IsEnabled == enabled) {
				return;
			}

			var broadPhase = W.GetResource<BroadPhase>();
			if (!enabled) {
				InvalidateBodyProxies(entity, broadPhase);
				body.Force = FVector3.Zero;
				body.Torque = FVector3.Zero;
				body.DeltaPosition = FVector3.Zero;
				body.DeltaRotation = FQuaternion.Identity;
				body.IsAwake = false;
				body.IsEnabled = false;
				body.EnableStateInitialized = true;
				return;
			}

			body.IsEnabled = true;
			body.EnableStateInitialized = true;
			body.Center = FWorldTransform.TransformPoint(body.Transform, body.LocalCenter);
			UpdateWorldInertia(ref body);
			Wake(ref body);
			var bodyType = body.Type;
			var bodyTransform = body.Transform;
			ForEachOwnedShape(entity, shapeEntity => {
				ref var shape = ref shapeEntity.Ref<Shape>();
				ShapeBroadPhaseOps.CreateProxy(ref shape, shapeEntity, broadPhase, bodyType, bodyTransform, true);
			});
		}

		public static void Enable(W.Entity entity) => SetEnabled(entity, true);
		public static void Disable(W.Entity entity) => SetEnabled(entity, false);

		public static void SetLinearVelocity(W.Entity entity, FVector3 velocity) {
			ref var body = ref RequireBody(entity);
			if (body.Type == BodyType.Static || !IsEnabled(body)) {
				return;
			}

			ApplyLinearLocks(ref velocity, body.MotionLocks);
			var maxSpeed = W.GetResource<PhysicsWorld>().MaximumLinearSpeed;
			var speedSquared = FVector3.LengthSqr(velocity);
			if (speedSquared > maxSpeed * maxSpeed) {
				velocity *= maxSpeed / FVector3.Length(velocity);
			}
			body.LinearVelocity = velocity;
			if (speedSquared > FP.Zero) {
				Wake(ref body);
			}
		}

		public static void SetAngularVelocity(W.Entity entity, FVector3 velocity) {
			ref var body = ref RequireBody(entity);
			if (body.Type == BodyType.Static || !IsEnabled(body)) {
				return;
			}

			ApplyAngularLocks(ref velocity, body.MotionLocks);
			body.AngularVelocity = velocity;
			if (FVector3.LengthSqr(velocity) > FP.Zero) {
				Wake(ref body);
			}
		}

		public static void SetVelocity(W.Entity entity, FVector3 linear, FVector3 angular) {
			SetLinearVelocity(entity, linear);
			SetAngularVelocity(entity, angular);
		}

		public static void ApplyLinearImpulseToCenter(W.Entity entity, FVector3 impulse) {
			ref var body = ref RequireDynamicBody(entity);
			body.LinearVelocity += body.InvMass * impulse;
			ApplyLinearLocks(ref body.LinearVelocity, body.MotionLocks);
			Wake(ref body);
		}

		public static void ApplyLinearImpulse(W.Entity entity, FVector3 impulse, FPos worldPoint) {
			ref var body = ref RequireDynamicBody(entity);
			body.LinearVelocity += body.InvMass * impulse;
			body.AngularVelocity += body.InvInertiaWorld * FVector3.Cross(worldPoint - body.Center, impulse);
			ApplyMotionLocks(ref body);
			Wake(ref body);
		}

		public static void ApplyAngularImpulse(W.Entity entity, FVector3 impulse) {
			ref var body = ref RequireDynamicBody(entity);
			body.AngularVelocity += body.InvInertiaWorld * impulse;
			ApplyAngularLocks(ref body.AngularVelocity, body.MotionLocks);
			Wake(ref body);
		}

		public static void ApplyForceToCenter(W.Entity entity, FVector3 force) {
			ref var body = ref RequireDynamicBody(entity);
			body.Force += force;
			Wake(ref body);
		}

		public static void ApplyForce(W.Entity entity, FVector3 force, FPos worldPoint) {
			ref var body = ref RequireDynamicBody(entity);
			body.Force += force;
			body.Torque += FVector3.Cross(worldPoint - body.Center, force);
			Wake(ref body);
		}

		public static void ApplyTorque(W.Entity entity, FVector3 torque) {
			ref var body = ref RequireDynamicBody(entity);
			body.Torque += torque;
			Wake(ref body);
		}

		private static ref Body RequireBody(W.Entity entity) {
			if (!entity.Has<Body>()) {
				throw new InvalidOperationException("Body operation requires an entity with Body.");
			}
			return ref entity.Ref<Body>();
		}

		private static ref Body RequireDynamicBody(W.Entity entity) {
			ref var body = ref RequireBody(entity);
			if (body.Type != BodyType.Dynamic || !IsEnabled(body)) {
				throw new InvalidOperationException("Forces and impulses require an enabled dynamic body.");
			}
			return ref body;
		}

		private static void ForEachOwnedShape(W.Entity bodyEntity, Action<W.Entity> action) {
			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				return;
			}
			ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
			for (var i = 0; i < links.Length; i++) {
				if (links[i].Value.TryUnpack<TWorld>(out var shapeEntity) && shapeEntity.Has<Shape>()) {
					action(shapeEntity);
				}
			}
		}

		private static void InvalidateBodyProxies(W.Entity bodyEntity, BroadPhase broadPhase) {
			ContactLifecycle.DestroyContactsForBody(bodyEntity, broadPhase);
			ForEachOwnedShape(bodyEntity, shapeEntity => {
				ref var shape = ref shapeEntity.Ref<Shape>();
				broadPhase.ForgetPairsForShape(shapeEntity.GID);
				ShapeBroadPhaseOps.DestroyProxy(ref shape, broadPhase);
			});
		}

		private static void SyncGameplayTransform(W.Entity entity, FWorldTransform transform) {
			if (entity.Has<Transform>()) {
				entity.Mut<Transform>().SetFromWorldTransform(transform);
			}
		}

		private static void UpdateWorldInertia(ref Body body) {
			var rotation = FMatrix3.FromQuaternion(body.Transform.Rotation);
			body.InvInertiaWorld = rotation * body.InvInertiaLocal * FMatrix3.Transpose(rotation);
		}

		private static void Wake(ref Body body) {
			if (body.Type != BodyType.Static) {
				body.IsAwake = true;
			}
		}

		private static void ApplyMotionLocks(ref Body body) {
			ApplyLinearLocks(ref body.LinearVelocity, body.MotionLocks);
			ApplyAngularLocks(ref body.AngularVelocity, body.MotionLocks);
		}

		internal static void ApplyLinearLocks(ref FVector3 value, MotionLocks locks) {
			if (locks.LinearX)
				value.X = FP.Zero;
			if (locks.LinearY)
				value.Y = FP.Zero;
			if (locks.LinearZ)
				value.Z = FP.Zero;
		}

		internal static void ApplyAngularLocks(ref FVector3 value, MotionLocks locks) {
			if (locks.AngularX)
				value.X = FP.Zero;
			if (locks.AngularY)
				value.Y = FP.Zero;
			if (locks.AngularZ)
				value.Z = FP.Zero;
		}
	}
}
