using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Box3d's sub-stepped "soft" contact solver (solver.c/contact_solver.c), ported single-threaded
	/// and without islands/solver-sets/sleeping — every awake body and touching contact is iterated
	/// directly each tick instead of being grouped for multithreading or memory locality. Also out of
	/// scope for this pass: joints, external forces/torques (no ApplyForce API yet), gyroscopic torque
	/// correction (box3d calls it "optional polish"), twist friction, rolling resistance, and
	/// conveyor-belt tangent velocity. Runs after <see cref="ContactSystem"/> each tick, so it always
	/// solves against that tick's freshly recomputed manifolds.
	/// </summary>
	public struct ContactSolverSystem : ISystem {
		private struct ContactConstraint {
			public W.Entity ContactEntity;
			public W.Entity BodyA;
			public W.Entity BodyB;
			public FVector3 Normal;
			public FVector3 Tangent1;
			public FVector3 Tangent2;

			/// <summary>World-frame anchor (center-of-mass relative), fixed at prepare time; rotated
			/// live by each body's accumulated <see cref="Body.DeltaRotation"/> during solving.</summary>
			public FVector3 RA;
			public FVector3 RB;
			public FP BaseSeparation;
			public FP NormalMass;

			// Pre-inverted 2x2 tangent mass matrix.
			public FP TangentMassXX;
			public FP TangentMassYY;
			public FP TangentMassXY;

			public FP Friction;
			public FP Restitution;

			/// <summary>Pre-solve relative normal velocity, captured once in Prepare, used by restitution.</summary>
			public FP RelativeVelocity;

			public FP NormalImpulse;
			public FP FrictionImpulseX;
			public FP FrictionImpulseY;
			public FP TotalNormalImpulse;
			public Softness Softness;
		}

		public void Update() {
			var world = Systems.GetResource<PhysicsWorld>();

			var dt = Const.DeltaTime.To32();
			var subStepCount = Math.Max(1, world.SubStepCount);
			var h = dt / subStepCount.ToFP();
			var invH = h > FP.Zero ? FP.One / h : FP.Zero;
			var invDt = Const.InvDeltaTime.To32();

			var contactHertz = FP.Min(world.ContactHertz, FP.FromRatio(1, 8) * invH);
			var contactSoftness = Softness.Make(contactHertz, world.ContactDampingRatio, h);
			var staticSoftness = Softness.Make(2 * contactHertz, FP.Half * world.ContactDampingRatio, h);

			var bodies = new List<W.Entity>();
			foreach (var entity in W.Query<All<Body>>().Entities()) {
				if (entity.Read<Body>().Type != BodyType.Static) {
					bodies.Add(entity);
				}
			}

			// Every broad-phase-overlapping Contact is solved, not just ones flagged Touching:
			// Touching is a gameplay/event concern (begin/end-touch events), while the solver also
			// needs "approaching but not yet touching" contacts so the speculative-margin branch in
			// Solve (s > 0) can cap closing velocity before the bodies actually overlap. Skipping
			// non-touching contacts here would let fast-moving bodies tunnel through in a single tick.
			var constraints = new List<ContactConstraint>();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				if (TryPrepare(contactEntity, world.EnableWarmStarting, contactSoftness, staticSoftness, out var constraint)) {
					constraints.Add(constraint);
				}
			}

			foreach (var entity in bodies) {
				ref var body = ref entity.Ref<Body>();
				body.DeltaPosition = FVector3.Zero;
				body.DeltaRotation = FQuaternion.Identity;
			}

			for (var substep = 0; substep < subStepCount; substep++) {
				IntegrateVelocities(bodies, h, world.Gravity);
				WarmStart(constraints);
				Solve(constraints, h, invH, world.ContactSpeed, useBias: true);
				IntegratePositions(bodies, h, world.MaximumLinearSpeed, invDt);
				Solve(constraints, h, invH, world.ContactSpeed, useBias: false);
			}

			ApplyRestitution(constraints, world.RestitutionThreshold);
			FinalizeBodies(bodies);
			StoreImpulses(constraints);
		}

		private static bool TryPrepare(W.Entity contactEntity, bool enableWarmStarting, Softness contactSoftness, Softness staticSoftness, out ContactConstraint constraint) {
			constraint = default;

			ref readonly var contact = ref contactEntity.Read<Contact>();
			ref readonly var shapeALink = ref contactEntity.Read<W.Link<ShapeA>>();
			ref readonly var shapeBLink = ref contactEntity.Read<W.Link<ShapeB>>();

			if (!shapeALink.Value.TryUnpack<TWorld>(out var shapeAEntity) || !shapeBLink.Value.TryUnpack<TWorld>(out var shapeBEntity)) {
				return false;
			}

			if (!TryGetBody(shapeAEntity, out var bodyAEntity) || !TryGetBody(shapeBEntity, out var bodyBEntity) || bodyAEntity == bodyBEntity) {
				return false;
			}

			// Distance.ShapeDistance can report a degenerate (zero-length) normal — it early-returns
			// "treat as overlap" when the GJK simplex fails to normalize (see its NormalTolerance
			// check). FVector3.Perp divides by the normal's assumed unit length, so skip solving this
			// contact for this tick rather than crash; it'll either resolve next tick as the bodies
			// separate a little, or get destroyed by ContactSystem once the AABBs stop overlapping.
			if (FVector3.LengthSqr(contact.Manifold.Normal) < FP.CalculationsEpsilonSqr) {
				return false;
			}

			ref readonly var shapeDataA = ref shapeAEntity.Read<Shape>();
			ref readonly var shapeDataB = ref shapeBEntity.Read<Shape>();
			ref readonly var bodyA = ref bodyAEntity.Read<Body>();
			ref readonly var bodyB = ref bodyBEntity.Read<Body>();

			// Manifold.Normal/Point0.Point are in shape A's local frame (Distance.ShapeDistance's
			// contract) — rotate/transform into world using bodyA's transform now, before this tick's
			// integration moves anything.
			var worldNormal = bodyA.Transform.Rotation * contact.Manifold.Normal;
			var worldPoint = FWorldTransform.TransformPoint(bodyA.Transform, contact.Manifold.Point0.Point);

			var rA = worldPoint - bodyA.Center;
			var rB = worldPoint - bodyB.Center;

			var baseSeparation = contact.Manifold.Point0.Separation - FVector3.Dot(rB - rA, worldNormal);

			var tangent1 = FVector3.Perp(worldNormal);
			var tangent2 = FVector3.Cross(tangent1, worldNormal);

			var invMassA = bodyA.InvMass;
			var invMassB = bodyB.InvMass;
			var invIA = bodyA.InvInertiaWorld;
			var invIB = bodyB.InvInertiaWorld;

			var rnA = FVector3.Cross(rA, worldNormal);
			var rnB = FVector3.Cross(rB, worldNormal);
			var kNormal = invMassA + invMassB + FVector3.Dot(rnA, invIA * rnA) + FVector3.Dot(rnB, invIB * rnB);
			var normalMass = kNormal > FP.Zero ? FP.One / kNormal : FP.Zero;

			var rtA1 = FVector3.Cross(rA, tangent1);
			var rtA2 = FVector3.Cross(rA, tangent2);
			var rtB1 = FVector3.Cross(rB, tangent1);
			var rtB2 = FVector3.Cross(rB, tangent2);

			var kxx = invMassA + invMassB + FVector3.Dot(rtA1, invIA * rtA1) + FVector3.Dot(rtB1, invIB * rtB1);
			var kyy = invMassA + invMassB + FVector3.Dot(rtA2, invIA * rtA2) + FVector3.Dot(rtB2, invIB * rtB2);
			var kxy = FVector3.Dot(rtA1, invIA * rtA2) + FVector3.Dot(rtB1, invIB * rtB2);

			var tangentDet = kxx * kyy - kxy * kxy;
			FP tangentMassXX = FP.Zero, tangentMassYY = FP.Zero, tangentMassXY = FP.Zero;
			if (tangentDet != FP.Zero) {
				var invDet = FP.One / tangentDet;
				tangentMassXX = kyy * invDet;
				tangentMassYY = kxx * invDet;
				tangentMassXY = -kxy * invDet;
			}

			var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, rA);
			var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, rB);
			var relativeVelocity = FVector3.Dot(worldNormal, vrB - vrA);

			var friction = FP.Sqrt(shapeDataA.Material.Friction * shapeDataB.Material.Friction);
			var restitution = FP.Max(shapeDataA.Material.Restitution, shapeDataB.Material.Restitution);
			var softness = bodyA.Type == BodyType.Static || bodyB.Type == BodyType.Static ? staticSoftness : contactSoftness;

			constraint = new ContactConstraint {
				ContactEntity = contactEntity,
				BodyA = bodyAEntity,
				BodyB = bodyBEntity,
				Normal = worldNormal,
				Tangent1 = tangent1,
				Tangent2 = tangent2,
				RA = rA,
				RB = rB,
				BaseSeparation = baseSeparation,
				NormalMass = normalMass,
				TangentMassXX = tangentMassXX,
				TangentMassYY = tangentMassYY,
				TangentMassXY = tangentMassXY,
				Friction = friction,
				Restitution = restitution,
				RelativeVelocity = relativeVelocity,
				NormalImpulse = enableWarmStarting ? contact.Manifold.Point0.NormalImpulse : FP.Zero,
				FrictionImpulseX = enableWarmStarting ? contact.FrictionImpulseX : FP.Zero,
				FrictionImpulseY = enableWarmStarting ? contact.FrictionImpulseY : FP.Zero,
				TotalNormalImpulse = FP.Zero,
				Softness = softness,
			};

			return true;
		}

		private static bool TryGetBody(W.Entity shapeEntity, out W.Entity bodyEntity) {
			bodyEntity = default;
			if (!shapeEntity.Has<W.Link<BodyOwner>>()) {
				return false;
			}

			ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
			return owner.Value.TryUnpack<TWorld>(out bodyEntity);
		}

		private static void IntegrateVelocities(List<W.Entity> bodies, FP h, FVector3 gravity) {
			foreach (var entity in bodies) {
				ref var body = ref entity.Ref<Body>();

				var gravityScale = body.InvMass > FP.Zero ? body.GravityScale : FP.Zero;
				var linearDamping = FP.One / (FP.One + h * body.LinearDamping);
				var angularDamping = FP.One / (FP.One + h * body.AngularDamping);

				// No force/torque accumulator yet (no ApplyForce API), so the only linear delta is gravity.
				body.LinearVelocity = h * gravityScale * gravity + linearDamping * body.LinearVelocity;
				body.AngularVelocity = angularDamping * body.AngularVelocity;
			}
		}

		private static void IntegratePositions(List<W.Entity> bodies, FP h, FP maxLinearSpeed, FP invDt) {
			var maxLinearSpeedSquared = maxLinearSpeed * maxLinearSpeed;
			var maxAngularSpeed = B3Config.MaxRotation * invDt;
			var maxAngularSpeedSquared = maxAngularSpeed * maxAngularSpeed;

			foreach (var entity in bodies) {
				ref var body = ref entity.Ref<Body>();

				var v = body.LinearVelocity;
				var w = body.AngularVelocity;

				if (body.MotionLocks.LinearX) v.X = FP.Zero;
				if (body.MotionLocks.LinearY) v.Y = FP.Zero;
				if (body.MotionLocks.LinearZ) v.Z = FP.Zero;
				if (body.MotionLocks.AngularX) w.X = FP.Zero;
				if (body.MotionLocks.AngularY) w.Y = FP.Zero;
				if (body.MotionLocks.AngularZ) w.Z = FP.Zero;

				if (FVector3.LengthSqr(v) > maxLinearSpeedSquared) {
					v *= maxLinearSpeed / FVector3.Length(v);
				}

				if (!body.AllowFastRotation && FVector3.LengthSqr(w) > maxAngularSpeedSquared) {
					w *= maxAngularSpeed / FVector3.Length(w);
				}

				body.LinearVelocity = v;
				body.AngularVelocity = w;
				body.DeltaPosition += h * v;
				body.DeltaRotation = FQuaternion.IntegrateRotation(body.DeltaRotation, h * w);
			}
		}

		private static void WarmStart(List<ContactConstraint> constraints) {
			foreach (var c in constraints) {
				var p = c.NormalImpulse * c.Normal + c.FrictionImpulseX * c.Tangent1 + c.FrictionImpulseY * c.Tangent2;
				ApplyImpulse(c.BodyA, c.BodyB, c.RA, c.RB, p);
			}
		}

		private static void Solve(List<ContactConstraint> constraints, FP h, FP invH, FP contactSpeed, bool useBias) {
			for (var i = 0; i < constraints.Count; i++) {
				var c = constraints[i];

				ref var bodyA = ref c.BodyA.Ref<Body>();
				ref var bodyB = ref c.BodyB.Ref<Body>();

				var dp = bodyB.DeltaPosition - bodyA.DeltaPosition;
				var ds = dp + (bodyB.DeltaRotation * c.RB - bodyA.DeltaRotation * c.RA);
				var s = FVector3.Dot(ds, c.Normal) + c.BaseSeparation;

				FP velocityBias = FP.Zero, massScale = FP.One, impulseScale = FP.Zero;
				if (s > FP.Zero) {
					// Speculative: not yet touching — cap approach velocity so this substep can't tunnel past the surface.
					velocityBias = s * invH;
				} else if (useBias) {
					velocityBias = FP.Max(c.Softness.MassScale * c.Softness.BiasRate * s, -contactSpeed);
					massScale = c.Softness.MassScale;
					impulseScale = c.Softness.ImpulseScale;
				}

				var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, c.RA);
				var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, c.RB);
				var vn = FVector3.Dot(vrB - vrA, c.Normal);

				var deltaImpulse = -c.NormalMass * (massScale * vn + velocityBias) - impulseScale * c.NormalImpulse;
				var newImpulse = FP.Max(c.NormalImpulse + deltaImpulse, FP.Zero);
				deltaImpulse = newImpulse - c.NormalImpulse;
				c.NormalImpulse = newImpulse;
				c.TotalNormalImpulse += newImpulse;

				var p = deltaImpulse * c.Normal;
				bodyA.LinearVelocity -= bodyA.InvMass * p;
				bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(c.RA, p);
				bodyB.LinearVelocity += bodyB.InvMass * p;
				bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(c.RB, p);

				// Friction only during the unbiased "relax" pass, matching box3d.
				if (!useBias) {
					vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, c.RA);
					vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, c.RB);
					var vr = vrB - vrA;
					var vtX = FVector3.Dot(vr, c.Tangent1);
					var vtY = FVector3.Dot(vr, c.Tangent2);

					var dImpulseX = -(c.TangentMassXX * vtX + c.TangentMassXY * vtY);
					var dImpulseY = -(c.TangentMassXY * vtX + c.TangentMassYY * vtY);

					var newX = c.FrictionImpulseX + dImpulseX;
					var newY = c.FrictionImpulseY + dImpulseY;

					// Box-clamp to the friction cone rather than box3d's precise Euclidean (disc)
					// clamp: Fixed32.FP is Q16.16 (32-bit raw, ~32767 range) — squaring an impulse
					// component past ~181 silently overflows and can wrap to negative, which previously
					// crashed FP.Sqrt below (hit in practice from bodies spawned overlapping, producing
					// large corrective impulses). A box clamp needs no squaring at all, so it can't
					// overflow this way; the only cost is friction can be up to ~1.41x stronger exactly
					// on the diagonal between tangent1/tangent2, which doesn't affect stability.
					var maxImpulse = FP.Abs(c.Friction * c.TotalNormalImpulse);
					newX = FP.Clamp(newX, -maxImpulse, maxImpulse);
					newY = FP.Clamp(newY, -maxImpulse, maxImpulse);

					dImpulseX = newX - c.FrictionImpulseX;
					dImpulseY = newY - c.FrictionImpulseY;
					c.FrictionImpulseX = newX;
					c.FrictionImpulseY = newY;

					var p2 = dImpulseX * c.Tangent1 + dImpulseY * c.Tangent2;
					bodyA.LinearVelocity -= bodyA.InvMass * p2;
					bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(c.RA, p2);
					bodyB.LinearVelocity += bodyB.InvMass * p2;
					bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(c.RB, p2);
				}

				constraints[i] = c;
			}
		}

		private static void ApplyRestitution(List<ContactConstraint> constraints, FP restitutionThreshold) {
			for (var i = 0; i < constraints.Count; i++) {
				var c = constraints[i];

				if (c.Restitution == FP.Zero || c.RelativeVelocity > -restitutionThreshold || c.TotalNormalImpulse == FP.Zero) {
					continue;
				}

				ref var bodyA = ref c.BodyA.Ref<Body>();
				ref var bodyB = ref c.BodyB.Ref<Body>();

				var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, c.RA);
				var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, c.RB);
				var vn = FVector3.Dot(vrB - vrA, c.Normal);

				var impulse = -c.NormalMass * (vn + c.Restitution * c.RelativeVelocity);
				var newImpulse = FP.Max(c.NormalImpulse + impulse, FP.Zero);
				impulse = newImpulse - c.NormalImpulse;
				c.NormalImpulse = newImpulse;
				c.TotalNormalImpulse += impulse;

				var p = impulse * c.Normal;
				bodyA.LinearVelocity -= bodyA.InvMass * p;
				bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(c.RA, p);
				bodyB.LinearVelocity += bodyB.InvMass * p;
				bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(c.RB, p);

				constraints[i] = c;
			}
		}

		private static void FinalizeBodies(List<W.Entity> bodies) {
			foreach (var entity in bodies) {
				// Mut, not Ref: this is the one place Transform actually changes, and
				// ShapeProxySystem's AABB-refresh pass only reacts to AllChanged<Body>.
				ref var body = ref entity.Mut<Body>();

				body.Center += body.DeltaPosition;
				body.Transform.Rotation = FQuaternion.Normalize(body.DeltaRotation * body.Transform.Rotation);
				body.Transform.Position = body.Center + -(body.Transform.Rotation * body.LocalCenter);

				body.DeltaPosition = FVector3.Zero;
				body.DeltaRotation = FQuaternion.Identity;

				// Rotation changed — refresh the world-frame inverse inertia for next tick's solve.
				var rotationMatrix = FMatrix3.FromQuaternion(body.Transform.Rotation);
				body.InvInertiaWorld = rotationMatrix * body.InvInertiaLocal * FMatrix3.Transpose(rotationMatrix);
			}
		}

		private static void StoreImpulses(List<ContactConstraint> constraints) {
			foreach (var c in constraints) {
				ref var contact = ref c.ContactEntity.Ref<Contact>();
				contact.Manifold.Point0.NormalImpulse = c.NormalImpulse;
				contact.FrictionImpulseX = c.FrictionImpulseX;
				contact.FrictionImpulseY = c.FrictionImpulseY;
			}
		}

		private static void ApplyImpulse(W.Entity bodyAEntity, W.Entity bodyBEntity, FVector3 rA, FVector3 rB, FVector3 p) {
			ref var bodyA = ref bodyAEntity.Ref<Body>();
			ref var bodyB = ref bodyBEntity.Ref<Body>();

			bodyA.LinearVelocity -= bodyA.InvMass * p;
			bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(rA, p);
			bodyB.LinearVelocity += bodyB.InvMass * p;
			bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(rB, p);
		}
	}
}
