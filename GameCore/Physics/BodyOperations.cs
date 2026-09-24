using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using Matrix64 = Fixed64.FMatrix3;
using Vector64 = Fixed64.FVector3;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>Safe mutation boundary for body state that has physics-derived data.</summary>
	public static class BodyOperations {
		public static bool IsEnabled(in Body body) => !body.EnableStateInitialized || body.IsEnabled;

		public static void CreateBody(W.Entity entity, BodyType type, FWorldTransform transform) {
			B3Config.Freeze();
			if (entity.Has<Body>()) {
				throw new InvalidOperationException("BodyOperations.CreateBody cannot overwrite an existing body.");
			}
			if (entity.Has<W.Links<Shapes>>() && entity.Read<W.Links<Shapes>>().Length > 0) {
				throw new InvalidOperationException("A body must be created before its shapes.");
			}

			PhysicsValidation.ValidateTransform(transform, nameof(transform));
			transform.Rotation = FQuaternion.Normalize(transform.Rotation);
			entity.Set(new Body {
				Type = type,
				Transform = transform,
				Center = transform.Position,
				GravityScale = FP.One,
				EnableSleep = true,
				IsAwake = true,
				SleepThreshold = Body.DefaultSleepThreshold,
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
			PhysicsValidation.ValidateTransform(transform, nameof(transform));
			transform.Rotation = FQuaternion.Normalize(transform.Rotation);
			ValidateOwnedShapes(entity, body.Type, transform);
			body.Transform = transform;
			body.Center = FWorldTransform.TransformPoint(transform, body.LocalCenter);
			body.DeltaPosition = FVector3.Zero;
			body.DeltaRotation = FQuaternion.Identity;
			UpdateWorldInertia(ref body);
			Wake(ref body);
			SyncGameplayTransform(entity, transform);
			// Setting a body's transform directly is a jump by definition; continuous motion goes
			// through velocity. Stamp it so render interpolation snaps instead of sliding.
			if (entity.Has<Transform>()) {
				entity.Mut<Transform>().MarkTeleported(S.CurrentTick);
			}
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
			ValidateOwnedShapes(entity, type, body.Transform);
			var originalBody = body;
			try {
				body.Type = type;
				BodyMassUpdate.Update(entity);
			} finally {
				body = originalBody;
			}

			var broadPhase = W.GetResource<BroadPhase>();
			InvalidateBodyProxies(entity, broadPhase);

			body.Type = type;
			if (type == BodyType.Static) {
				body.IsBullet = false;
			}
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

			// A body parked outside the escape line would be disabled again by FinalizeBodies only after
			// it had moved another tick outward, so re-enabling it there is rejected up front.
			if (body.Type != BodyType.Static && !PhysicsValidation.IsInsideSimulationBounds(body.Transform.Position)) {
				throw new InvalidOperationException("Cannot enable a body outside the physics simulation bounds; move it back inside first.");
			}

			body.IsEnabled = true;
			body.EnableStateInitialized = true;
			entity.Delete<OutOfPhysicsBounds>();
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

		/// <summary>Wakes a body; the rest of its island wakes before the next solver step.</summary>
		public static void Wake(W.Entity entity) {
			ref var body = ref RequireBody(entity);
			Wake(ref body);
		}

		/// <summary>Whether the body is enabled, non-static, and currently asleep.</summary>
		public static bool IsSleeping(W.Entity entity) => PhysicsSleep.IsSleeping(RequireBody(entity));

		/// <summary>Allows or forbids this body to fall asleep. Forbidding sleep wakes it.</summary>
		public static void SetSleepEnabled(W.Entity entity, bool enableSleep) {
			ref var body = ref RequireBody(entity);
			body.EnableSleep = enableSleep;
			if (!enableSleep) {
				Wake(ref body);
			}
		}

		/// <summary>Sets the resting surface speed below which the body accumulates sleep time.</summary>
		public static void SetSleepThreshold(W.Entity entity, FP sleepThreshold) {
			ref var body = ref RequireBody(entity);
			if (sleepThreshold < FP.Zero || sleepThreshold > PhysicsValidation.MaximumLinearSpeed) {
				throw new ArgumentOutOfRangeException(nameof(sleepThreshold), "Sleep threshold must be between zero and the maximum linear speed.");
			}
			body.SleepThreshold = sleepThreshold;
		}

		public static void Enable(W.Entity entity) => SetEnabled(entity, true);
		public static void Disable(W.Entity entity) => SetEnabled(entity, false);

		public static void SetLinearVelocity(W.Entity entity, FVector3 velocity) {
			ref var body = ref RequireBody(entity);
			if (body.Type == BodyType.Static || !IsEnabled(body)) {
				return;
			}

			ApplyLinearLocks(ref velocity, body.MotionLocks);
			body.LinearVelocity = ClampLinear(velocity.To64());
			if (FVector3.LengthSqr(body.LinearVelocity) > FP.Zero) {
				Wake(ref body);
			}
		}

		public static void SetAngularVelocity(W.Entity entity, FVector3 velocity) {
			ref var body = ref RequireBody(entity);
			if (body.Type == BodyType.Static || !IsEnabled(body)) {
				return;
			}

			ApplyAngularLocks(ref velocity, body.MotionLocks);
			body.AngularVelocity = ClampAngular(body, velocity.To64());
			if (FVector3.LengthSqr(body.AngularVelocity) > FP.Zero) {
				Wake(ref body);
			}
		}

		public static void SetVelocity(W.Entity entity, FVector3 linear, FVector3 angular) {
			SetLinearVelocity(entity, linear);
			SetAngularVelocity(entity, angular);
		}

		/// <summary>
		/// Enables continuous collision detection against static, kinematic, and non-bullet dynamic
		/// bodies. Dynamic bodies also receive automatic CCD against static geometry when their motion
		/// exceeds their smallest shape extent.
		/// </summary>
		public static void SetBullet(W.Entity entity, bool isBullet) {
			ref var body = ref RequireBody(entity);
			if (isBullet && body.Type == BodyType.Static) {
				throw new InvalidOperationException("Static bodies cannot be bullets.");
			}
			body.IsBullet = isBullet;
			Wake(ref body);
		}

		public static void ApplyLinearImpulseToCenter(W.Entity entity, FVector3 impulse) {
			ref var body = ref RequireDynamicBody(entity);
			body.LinearVelocity = ClampLinear(LinearAfterImpulse(body, impulse));
			Wake(ref body);
		}

		public static void ApplyLinearImpulse(W.Entity entity, FVector3 impulse, FPos worldPoint) {
			ref var body = ref RequireDynamicBody(entity);
			PhysicsValidation.ValidatePosition(worldPoint, nameof(worldPoint));
			var offset = new Vector64(worldPoint.X - body.Center.X, worldPoint.Y - body.Center.Y, worldPoint.Z - body.Center.Z);
			var angular = AngularAfterImpulse(body, Vector64.Cross(offset, impulse.To64()));
			body.LinearVelocity = ClampLinear(LinearAfterImpulse(body, impulse));
			body.AngularVelocity = ClampAngular(body, angular);
			Wake(ref body);
		}

		public static void ApplyAngularImpulse(W.Entity entity, FVector3 impulse) {
			ref var body = ref RequireDynamicBody(entity);
			body.AngularVelocity = ClampAngular(body, AngularAfterImpulse(body, impulse.To64()));
			Wake(ref body);
		}

		public static void ApplyForceToCenter(W.Entity entity, FVector3 force) {
			ref var body = ref RequireDynamicBody(entity);
			body.Force = ClampForceOrTorque(body.Force.To64() + ClampForceOrTorque(force.To64()).To64());
			Wake(ref body);
		}

		public static void ApplyForce(W.Entity entity, FVector3 force, FPos worldPoint) {
			ref var body = ref RequireDynamicBody(entity);
			PhysicsValidation.ValidatePosition(worldPoint, nameof(worldPoint));
			var clampedForce = ClampForceOrTorque(force.To64()).To64();
			var offset = new Vector64(worldPoint.X - body.Center.X, worldPoint.Y - body.Center.Y, worldPoint.Z - body.Center.Z);
			body.Force = ClampForceOrTorque(body.Force.To64() + clampedForce);
			body.Torque = ClampForceOrTorque(body.Torque.To64() + ClampForceOrTorque(Vector64.Cross(offset, clampedForce)).To64());
			Wake(ref body);
		}

		public static void ApplyTorque(W.Entity entity, FVector3 torque) {
			ref var body = ref RequireDynamicBody(entity);
			body.Torque = ClampForceOrTorque(body.Torque.To64() + ClampForceOrTorque(torque.To64()).To64());
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

		private static void ValidateOwnedShapes(W.Entity bodyEntity, BodyType type, FWorldTransform transform) {
			ForEachOwnedShape(bodyEntity, shapeEntity => {
				ref readonly var shape = ref shapeEntity.Read<Shape>();
				PhysicsValidation.ValidateShape(shape, type, transform, nameof(transform));
			});
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

		private static void Wake(ref Body body) => PhysicsSleep.WakeBody(ref body);

		// Velocity writes clamp to the same limits the solver applies in IntegratePositions, so any state
		// the solver can produce stays acceptable input here. Sums are formed in Fixed64 so a large impulse
		// saturates at the clamp instead of wrapping.
		private static Vector64 LinearAfterImpulse(in Body body, FVector3 impulse) {
			var velocity = body.LinearVelocity.To64() + body.InvMass.To64() * impulse.To64();
			var locks = body.MotionLocks;
			if (locks.LinearX)
				velocity.X = Fixed64.FP.Zero;
			if (locks.LinearY)
				velocity.Y = Fixed64.FP.Zero;
			if (locks.LinearZ)
				velocity.Z = Fixed64.FP.Zero;
			return velocity;
		}

		private static Vector64 AngularAfterImpulse(in Body body, Vector64 angularImpulse) {
			var inverseInertia = new Matrix64(body.InvInertiaWorld.Cx.To64(), body.InvInertiaWorld.Cy.To64(), body.InvInertiaWorld.Cz.To64());
			var velocity = body.AngularVelocity.To64() + inverseInertia * angularImpulse;
			var locks = body.MotionLocks;
			if (locks.AngularX)
				velocity.X = Fixed64.FP.Zero;
			if (locks.AngularY)
				velocity.Y = Fixed64.FP.Zero;
			if (locks.AngularZ)
				velocity.Z = Fixed64.FP.Zero;
			return velocity;
		}

		private static FVector3 ClampLinear(Vector64 velocity) =>
			PhysicsValidation.ClampMagnitude(velocity, W.GetResource<PhysicsWorld>().MaximumLinearSpeed);

		private static FVector3 ClampAngular(in Body body, Vector64 velocity) =>
			PhysicsValidation.ClampMagnitude(velocity, MaximumAngularSpeed(body));

		// Forces and torques saturate like impulses: inputs and accumulated sums are clamped in Fixed64
		// so the Fixed32 accumulators and IntegrateVelocities' products cannot wrap.
		private static FVector3 ClampForceOrTorque(Vector64 value) =>
			PhysicsValidation.ClampMagnitude(value, PhysicsValidation.MaximumForceOrTorque);

		/// <summary>The angular speed cap ContactSolverSystem.IntegratePositions applies to this body.</summary>
		internal static FP MaximumAngularSpeed(in Body body) =>
			body.AllowFastRotation ? PhysicsValidation.MaximumFastAngularSpeed : B3Config.MaxRotation * Const.InvDeltaTime.To32();

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
