using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

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
		/// <summary>Per-manifold-point normal constraint row; friction stays manifold-level (see <see cref="ContactConstraint.FrictionAnchorRA"/>), matching box3d's single friction anchor per manifold.</summary>
		private struct ContactConstraintPoint {
			/// <summary>World-frame anchor (center-of-mass relative), fixed at prepare time; rotated
			/// live by each body's accumulated <see cref="Body.DeltaRotation"/> during solving.</summary>
			public FVector3 RA;
			public FVector3 RB;
			public FP BaseSeparation;
			public FP NormalMass;

			/// <summary>Pre-solve relative normal velocity, captured once in Prepare, used by restitution.</summary>
			public FP RelativeVelocity;

			public FP NormalImpulse;
			public FP TotalNormalImpulse;
		}

		private struct ContactConstraint {
			public W.Entity ContactEntity;
			public W.Entity BodyA;
			public W.Entity BodyB;
			public FVector3 Normal;
			public FVector3 Tangent1;
			public FVector3 Tangent2;
			public EntityGID ShapeA;
			public EntityGID ShapeB;
			public bool EnableHitEvents;

			public int PointCount;
			public ContactConstraintPoint Point0;
			public ContactConstraintPoint Point1;
			public ContactConstraintPoint Point2;
			public ContactConstraintPoint Point3;

			/// <summary>Friction is solved once per manifold, not per point (box3d's single friction/
			/// twist anchor per manifold), through the average of every point's RA/RB.</summary>
			public FVector3 FrictionAnchorRA;
			public FVector3 FrictionAnchorRB;

			// Pre-inverted 2x2 tangent mass matrix, computed at the friction anchor.
			public FP TangentMassXX;
			public FP TangentMassYY;
			public FP TangentMassXY;

			public FP Friction;
			public FP Restitution;

			public FP FrictionImpulseX;
			public FP FrictionImpulseY;
			public Softness Softness;

			/// <summary>
			/// Rolling resistance: a Coulomb-limited torque that drives the bodies' relative angular
			/// velocity toward zero, ported from box3d's contact_solver.c. Zero (the common case,
			/// since <see cref="SurfaceMaterial.RollingResistance"/> defaults to zero) skips the block
			/// in <see cref="Solve"/> entirely.
			/// </summary>
			public FP RollingResistance;

			/// <summary>Full 3x3 effective inverse-inertia-sum mass for the rolling-resistance constraint (not projected onto a 2D basis, unlike friction).</summary>
			public FMatrix3 RollingMass;

			public FVector3 RollingImpulse;

			public ContactConstraintPoint GetPoint(int index) {
				return index switch {
					0 => Point0,
					1 => Point1,
					2 => Point2,
					_ => Point3,
				};
			}

			public void SetPoint(int index, ContactConstraintPoint point) {
				switch (index) {
					case 0:
						Point0 = point;
						break;
					case 1:
						Point1 = point;
						break;
					case 2:
						Point2 = point;
						break;
					default:
						Point3 = point;
						break;
				}
			}
		}

		public void Update() {
			var world = W.GetResource<PhysicsWorld>();
			PhysicsValidation.ValidateWorld(world);

			var dt = Const.DeltaTime.To32();
			var subStepCount = world.SubStepCount;
			var h = dt / subStepCount.ToFP();
			var invH = h > FP.Zero ? FP.One / h : FP.Zero;
			var invDt = Const.InvDeltaTime.To32();

			var contactHertz = FP.Min(world.ContactHertz, FP.FromRatio(1, 8) * invH);
			var contactSoftness = Softness.Make(contactHertz, world.ContactDampingRatio, h);
			var staticSoftness = Softness.Make(2 * contactHertz, FP.Half * world.ContactDampingRatio, h);

			var bodies = new List<W.Entity>();
			foreach (var entity in W.Query<All<Body>>().Entities()) {
				ref readonly var body = ref entity.Read<Body>();
				if (body.Type != BodyType.Static && BodyOperations.IsEnabled(body)) {
					bodies.Add(entity);
				}
			}

			// Every broad-phase-overlapping Contact is solved, not just ones flagged Touching:
			// Touching is a gameplay/event concern (begin/end-touch events), while the solver also
			// needs "approaching but not yet touching" contacts so the speculative-margin branch in
			// Solve (s > 0) can cap closing velocity before the bodies actually overlap. Skipping
			// non-touching contacts here would let fast-moving bodies tunnel through in a single tick.
			// Require both links in the filter itself (not just Contact) -- ContactSystem self-heals
			// any Contact entity missing a link, but since it runs earlier in the same tick's
			// pipeline rather than relying on that ordering, guard here too.
			var constraints = new List<ContactConstraint>();
#pragma warning disable FFSECS0050 // Link<ShapeA> and Link<ShapeB> are distinct relation types; the analyzer's duplicate check compares by open-generic definition and can't tell them apart.
			foreach (var contactEntity in W.Query<All<Contact, W.Link<ShapeA>, W.Link<ShapeB>>>().Entities()) {
#pragma warning restore FFSECS0050
				if (TryPrepare(contactEntity, world.EnableWarmStarting, contactSoftness, staticSoftness, out var constraint)) {
					constraints.Add(constraint);
				}
			}

			foreach (var entity in bodies) {
				ref var body = ref entity.Ref<Body>()!; // bodies is built from a Query<All<Body>> filter.
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
			SolveContinuousCollisions(bodies, W.GetResource<BroadPhase>());
			StoreImpulses(constraints, world.HitEventThreshold);
			FinalizeBodies(bodies);
		}

		private static void SolveContinuousCollisions(List<W.Entity> bodies, BroadPhase broadPhase) {
			bodies.Sort(static (a, b) => a.GID.Raw.CompareTo(b.GID.Raw));
			var candidates = new List<EntityGID>();
			for (var bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++) {
				var bodyEntity = bodies[bodyIndex];
				if (!bodyEntity.Has<Body>()) {
					continue;
				}
				ref var body = ref bodyEntity.Ref<Body>();
				if (!bodyEntity.Has<W.Links<Shapes>>() || !TryGetContinuousPolicy(bodyEntity, body, out var explicitBullet)) {
					continue;
				}

				var bestFraction = FP.One;
				var bestBulletShape = default(EntityGID);
				var bestTargetShape = default(EntityGID);
				var bestNormal = FVector3.Zero;
				var bestPoint = FPos.Zero;
				var bestTargetBody = default(W.Entity);
				var bestEnablesEvents = false;
				var bodyEndTransform = GetSweepEnd(body);
				ref readonly var shapeLinks = ref bodyEntity.Read<W.Links<Shapes>>();
				for (var shapeIndex = 0; shapeIndex < shapeLinks.Length; shapeIndex++) {
					if (!shapeLinks[shapeIndex].Value.TryUnpack<TWorld>(out var bulletShapeEntity) || !bulletShapeEntity.Has<Shape>()) {
						continue;
					}
					ref readonly var bulletShape = ref bulletShapeEntity.Read<Shape>();
					if (bulletShape.IsSensor) {
						continue;
					}

					var sweptAabb = ComputeSweptAabb(body, bulletShape);

					candidates.Clear();
					broadPhase.CollectProxies(candidates);
					candidates.Sort(static (a, b) => a.Raw.CompareTo(b.Raw));
					for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++) {
						var targetGid = candidates[candidateIndex];
						if (targetGid == bulletShapeEntity.GID || !targetGid.TryUnpack<TWorld>(out var targetShapeEntity)
							|| !targetShapeEntity.Has<Shape>() || !targetShapeEntity.Has<W.Link<BodyOwner>>()) {
							continue;
						}
						ref readonly var targetShape = ref targetShapeEntity.Read<Shape>();
						ref readonly var targetOwner = ref targetShapeEntity.Read<W.Link<BodyOwner>>();
						if (targetShape.IsSensor || !Filter.ShouldCollide(bulletShape.Filter, targetShape.Filter)
							|| !targetOwner.Value.TryUnpack<TWorld>(out var targetBodyEntity) || targetBodyEntity == bodyEntity
							|| !targetBodyEntity.Has<Body>()) {
							continue;
						}
						ref readonly var targetBody = ref targetBodyEntity.Read<Body>();
						if (!BodyOperations.IsEnabled(targetBody) || targetBody.IsBullet
							|| (!explicitBullet && targetBody.Type != BodyType.Static)) {
							continue;
						}
						var targetEndTransform = GetSweepEnd(targetBody);
						var targetSweptAabb = ComputeSweptAabb(targetBody, targetShape);
						if (!FAABB.Overlaps(sweptAabb, targetSweptAabb)) {
							continue;
						}

						var output = Distance.TimeOfImpact(new TimeOfImpactInput {
							ProxyA = targetShape.MakeProxy(),
							ProxyB = bulletShape.MakeProxy(),
							TransformAStart = targetBody.Transform,
							TransformAEnd = targetEndTransform,
							LocalCenterA = targetBody.LocalCenter,
							TransformBStart = body.Transform,
							TransformBEnd = bodyEndTransform,
							LocalCenterB = body.LocalCenter,
							MaxFraction = bestFraction,
						});
						var usableHit = output.State == TimeOfImpactState.Hit
							|| (output.State == TimeOfImpactState.Failed && output.Fraction > FP.Zero);
						if (!usableHit || output.Fraction <= FP.Zero || output.Fraction >= bestFraction) {
							continue;
						}

						bestFraction = output.Fraction;
						bestBulletShape = bulletShapeEntity.GID;
						bestTargetShape = targetGid;
						bestNormal = output.Normal;
						bestPoint = output.Point;
						bestTargetBody = targetBodyEntity;
						bestEnablesEvents = bulletShape.EnableContactEvents || targetShape.EnableContactEvents;
					}
				}

				if (bestFraction < FP.One) {
					body.DeltaPosition *= bestFraction;
					body.DeltaRotation = FQuaternion.Nlerp(FQuaternion.Identity, body.DeltaRotation, bestFraction);
					if (!bestTargetBody.Has<Body>()) {
						continue;
					}
					ref readonly var targetBody = ref bestTargetBody.Read<Body>();
					var bodyCenter = body.Center + body.DeltaPosition;
					var targetCenter = targetBody.Center + bestFraction * targetBody.DeltaPosition;
					var bodyPointVelocity = body.LinearVelocity + FVector3.Cross(body.AngularVelocity, bestPoint - bodyCenter);
					var targetPointVelocity = targetBody.LinearVelocity + FVector3.Cross(targetBody.AngularVelocity, bestPoint - targetCenter);
					var relativeNormalVelocity = FVector3.Dot(bodyPointVelocity - targetPointVelocity, bestNormal);
					if (relativeNormalVelocity < FP.Zero) {
						body.LinearVelocity -= relativeNormalVelocity * bestNormal;
					}
					if (explicitBullet && bestEnablesEvents) {
						W.SendEvent(new ContinuousHitEvent {
							BulletShape = bestBulletShape,
							TargetShape = bestTargetShape,
							Point = bestPoint,
							Normal = bestNormal,
							Fraction = bestFraction,
						});
					}
				}
			}
		}

		private static bool TryGetContinuousPolicy(W.Entity bodyEntity, in Body body, out bool explicitBullet) {
			explicitBullet = body.IsBullet && body.Type != BodyType.Static;
			if (explicitBullet) {
				return body.DeltaPosition != FVector3.Zero || !FQuaternion.ApproximatelyEqual(body.DeltaRotation, FQuaternion.Identity);
			}
			if (body.Type != BodyType.Dynamic || !TryGetBodyExtents(bodyEntity, body.LocalCenter, out var minExtent, out var maxExtent)) {
				return false;
			}

			var deltaRotation = body.DeltaRotation.W < FP.Zero ? -body.DeltaRotation : body.DeltaRotation;
			var maxMotion = FVector3.Length(body.DeltaPosition) + FQuaternion.GetAngle(deltaRotation) * maxExtent;
			return maxMotion > FP.Half * minExtent;
		}

		private static bool TryGetBodyExtents(W.Entity bodyEntity, FVector3 localCenter, out FP minExtent, out FP maxExtent) {
			minExtent = FP.MaxValue;
			maxExtent = FP.Zero;
			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				return false;
			}
			var found = false;
			ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
			for (var i = 0; i < links.Length; i++) {
				if (!links[i].Value.TryUnpack<TWorld>(out var shapeEntity) || !shapeEntity.Has<Shape>()) {
					continue;
				}
				ref readonly var shape = ref shapeEntity.Read<Shape>();
				if (shape.IsSensor) {
					continue;
				}
				var shapeMin = shape.ComputeMinimumExtent();
				if (shapeMin <= FP.Zero) {
					continue;
				}
				found = true;
				minExtent = FP.Min(minExtent, shapeMin);
				maxExtent = FP.Max(maxExtent, shape.ComputeSweepRadius(localCenter));
			}
			return found;
		}

		private static FWorldTransform GetSweepEnd(in Body body) {
			var deltaRotation = FQuaternion.NormalizeSafe(body.DeltaRotation, FQuaternion.Identity);
			var rotation = FQuaternion.Normalize(deltaRotation * body.Transform.Rotation);
			var center = body.Center + body.DeltaPosition;
			return new FWorldTransform(center + -(rotation * body.LocalCenter), rotation);
		}

		private static FAABB ComputeSweptAabb(in Body body, in Shape shape) {
			var radiusValue = shape.ComputeSweepRadius(body.LocalCenter) + B3Config.SpeculativeDistance;
			var radius = new FVector3(radiusValue, radiusValue, radiusValue);
			var start = new FVector3(body.Center.X.To32(), body.Center.Y.To32(), body.Center.Z.To32());
			var end = start + body.DeltaPosition;
			return new FAABB(FVector3.MinComponents(start, end) - radius, FVector3.MaxComponents(start, end) + radius);
		}

		private static bool TryPrepare(W.Entity contactEntity, bool enableWarmStarting, Softness contactSoftness, Softness staticSoftness, out ContactConstraint constraint) {
			constraint = default;

			// contactEntity always comes from Query<All<Contact, Link<ShapeA>, Link<ShapeB>>> in Update().
			ref readonly var contact = ref contactEntity.Read<Contact>()!;
			ref readonly var shapeALink = ref contactEntity.Read<W.Link<ShapeA>>()!;
			ref readonly var shapeBLink = ref contactEntity.Read<W.Link<ShapeB>>()!;

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
			ref readonly var manifold = ref contact.Manifold;
			if (manifold.PointCount == 0 || FVector3.LengthSqr(manifold.Normal) < FP.CalculationsEpsilonSqr) {
				return false;
			}

			ref readonly var shapeDataA = ref shapeAEntity.Read<Shape>()!; // Link<ShapeA>/<ShapeB> always resolve to shape entities.
			ref readonly var shapeDataB = ref shapeBEntity.Read<Shape>()!;

			// A sensor never participates in collision response (Shape.IsSensor's own doc comment) --
			// it still gets a manifold and touch events out of ContactSystem (which doesn't check this
			// flag, on purpose: sensors need overlap detection too), but the solver must not turn that
			// manifold into an impulse. Matches box3d's sensors never entering the solid-contact graph.
			if (contact.IsSensorContact || contact.IsEventOnly || shapeDataA.IsSensor || shapeDataB.IsSensor) {
				return false;
			}

			ref readonly var bodyA = ref bodyAEntity.Read<Body>()!; // TryGetBody only resolves entities with Body.
			ref readonly var bodyB = ref bodyBEntity.Read<Body>()!;
			if (!BodyOperations.IsEnabled(bodyA) || !BodyOperations.IsEnabled(bodyB)) {
				return false;
			}

			// Manifold.Normal/points are in shape A's local frame (Distance.ShapeDistance's
			// contract) — rotate/transform into world using bodyA's transform now, before this tick's
			// integration moves anything.
			var worldNormal = bodyA.Transform.Rotation * manifold.Normal;

			var invMassA = bodyA.InvMass;
			var invMassB = bodyB.InvMass;
			var invIA = bodyA.InvInertiaWorld;
			var invIB = bodyB.InvInertiaWorld;

			constraint.ContactEntity = contactEntity;
			constraint.BodyA = bodyAEntity;
			constraint.BodyB = bodyBEntity;
			constraint.Normal = worldNormal;
			constraint.ShapeA = shapeAEntity.GID;
			constraint.ShapeB = shapeBEntity.GID;
			constraint.EnableHitEvents = shapeDataA.EnableHitEvents || shapeDataB.EnableHitEvents;
			constraint.PointCount = manifold.PointCount;

			// Friction is solved once per manifold, through the average anchor of every point (matches
			// box3d), rather than per point.
			var frictionAnchorRA = FVector3.Zero;
			var frictionAnchorRB = FVector3.Zero;

			for (var i = 0; i < manifold.PointCount; i++) {
				var manifoldPoint = manifold.GetPoint(i);
				var worldPoint = FWorldTransform.TransformPoint(bodyA.Transform, manifoldPoint.Point);

				var rA = worldPoint - bodyA.Center;
				var rB = worldPoint - bodyB.Center;
				frictionAnchorRA += rA;
				frictionAnchorRB += rB;

				var baseSeparation = manifoldPoint.Separation - FVector3.Dot(rB - rA, worldNormal);

				var rnA = FVector3.Cross(rA, worldNormal);
				var rnB = FVector3.Cross(rB, worldNormal);
				var kNormal = invMassA + invMassB + FVector3.Dot(rnA, invIA * rnA) + FVector3.Dot(rnB, invIB * rnB);
				var normalMass = kNormal > FP.Zero ? FP.One / kNormal : FP.Zero;

				var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, rA);
				var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, rB);
				var relativeVelocity = FVector3.Dot(worldNormal, vrB - vrA);

				constraint.SetPoint(i, new ContactConstraintPoint {
					RA = rA,
					RB = rB,
					BaseSeparation = baseSeparation,
					NormalMass = normalMass,
					RelativeVelocity = relativeVelocity,
					NormalImpulse = enableWarmStarting ? manifoldPoint.NormalImpulse : FP.Zero,
					TotalNormalImpulse = FP.Zero,
				});
			}

			frictionAnchorRA /= manifold.PointCount;
			frictionAnchorRB /= manifold.PointCount;
			constraint.FrictionAnchorRA = frictionAnchorRA;
			constraint.FrictionAnchorRB = frictionAnchorRB;

			var tangent1 = FVector3.Perp(worldNormal);
			var tangent2 = FVector3.Cross(tangent1, worldNormal);
			constraint.Tangent1 = tangent1;
			constraint.Tangent2 = tangent2;

			var rtA1 = FVector3.Cross(frictionAnchorRA, tangent1);
			var rtA2 = FVector3.Cross(frictionAnchorRA, tangent2);
			var rtB1 = FVector3.Cross(frictionAnchorRB, tangent1);
			var rtB2 = FVector3.Cross(frictionAnchorRB, tangent2);

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

			constraint.TangentMassXX = tangentMassXX;
			constraint.TangentMassYY = tangentMassYY;
			constraint.TangentMassXY = tangentMassXY;

			constraint.Friction = FP.Sqrt(shapeDataA.Material.Friction * shapeDataB.Material.Friction);
			constraint.Restitution = FP.Max(shapeDataA.Material.Restitution, shapeDataB.Material.Restitution);
			var hasPersistedPoint = false;
			for (var i = 0; i < manifold.PointCount; i++) {
				hasPersistedPoint |= manifold.GetPoint(i).Persisted;
			}
			var frictionImpulse = enableWarmStarting && hasPersistedPoint ? contact.FrictionImpulse : FVector3.Zero;
			constraint.FrictionImpulseX = FVector3.Dot(frictionImpulse, tangent1);
			constraint.FrictionImpulseY = FVector3.Dot(frictionImpulse, tangent2);

			// Rolling resistance combining, matching box3d's b3UpdateConvexContact: the stronger of
			// the two materials' coefficients, scaled by whichever shape's own rolling radius is
			// larger (a sphere/capsule's radius, or a quarter of a hull's inner radius).
			constraint.RollingResistance = FP.Max(shapeDataA.Material.RollingResistance, shapeDataB.Material.RollingResistance)
				* FP.Max(shapeDataA.RollingRadius(), shapeDataB.RollingRadius());
			constraint.RollingMass = FMatrix3.Invert(invIA + invIB);
			constraint.RollingImpulse = enableWarmStarting && hasPersistedPoint ? contact.RollingImpulse : FVector3.Zero;
			constraint.Softness = bodyA.Type == BodyType.Static || bodyB.Type == BodyType.Static ? staticSoftness : contactSoftness;

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
				ref var body = ref entity.Ref<Body>()!; // bodies is built from a Query<All<Body>> filter.

				var gravityScale = body.InvMass > FP.Zero ? body.GravityScale : FP.Zero;
				var linearDamping = FP.One / (FP.One + h * body.LinearDamping);
				var angularDamping = FP.One / (FP.One + h * body.AngularDamping);

				body.LinearVelocity = h * gravityScale * gravity + body.InvMass * (h * body.Force) + linearDamping * body.LinearVelocity;
				body.AngularVelocity = body.InvInertiaWorld * (h * body.Torque) + angularDamping * body.AngularVelocity;

				// Locks before solving (box3d enforces them on every velocity write-back, e.g.
				// b3ScatterBodies): without this, the relax pass after IntegratePositions can leave
				// impulse-added velocity on locked axes stored in the body across ticks.
				BodyOperations.ApplyLinearLocks(ref body.LinearVelocity, body.MotionLocks);
				BodyOperations.ApplyAngularLocks(ref body.AngularVelocity, body.MotionLocks);
			}
		}

		private static void IntegratePositions(List<W.Entity> bodies, FP h, FP maxLinearSpeed, FP invDt) {
			var maxLinearSpeedSquared = maxLinearSpeed * maxLinearSpeed;
			var maxAngularSpeed = B3Config.MaxRotation * invDt;
			var maxAngularSpeedSquared = maxAngularSpeed * maxAngularSpeed;

			foreach (var entity in bodies) {
				ref var body = ref entity.Ref<Body>()!; // bodies is built from a Query<All<Body>> filter.

				var v = body.LinearVelocity;
				var w = body.AngularVelocity;

				if (body.MotionLocks.LinearX)
					v.X = FP.Zero;
				if (body.MotionLocks.LinearY)
					v.Y = FP.Zero;
				if (body.MotionLocks.LinearZ)
					v.Z = FP.Zero;
				if (body.MotionLocks.AngularX)
					w.X = FP.Zero;
				if (body.MotionLocks.AngularY)
					w.Y = FP.Zero;
				if (body.MotionLocks.AngularZ)
					w.Z = FP.Zero;

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
				for (var k = 0; k < c.PointCount; k++) {
					var pt = c.GetPoint(k);
					ApplyImpulse(c.BodyA, c.BodyB, pt.RA, pt.RB, pt.NormalImpulse * c.Normal);
				}

				var friction = c.FrictionImpulseX * c.Tangent1 + c.FrictionImpulseY * c.Tangent2;
				ApplyImpulse(c.BodyA, c.BodyB, c.FrictionAnchorRA, c.FrictionAnchorRB, friction);

				ApplyAngularImpulse(c.BodyA, c.BodyB, c.RollingImpulse);
			}
		}

		private static void Solve(List<ContactConstraint> constraints, FP h, FP invH, FP contactSpeed, bool useBias) {
			for (var i = 0; i < constraints.Count; i++) {
				var c = constraints[i];

				ref var bodyA = ref c.BodyA.Ref<Body>()!; // TryPrepare only stores entities with Body.
				ref var bodyB = ref c.BodyB.Ref<Body>()!;

				var dp = bodyB.DeltaPosition - bodyA.DeltaPosition;
				var totalNormalImpulse = FP.Zero;

				for (var k = 0; k < c.PointCount; k++) {
					var pt = c.GetPoint(k);

					var ds = dp + (bodyB.DeltaRotation * pt.RB - bodyA.DeltaRotation * pt.RA);
					var s = FVector3.Dot(ds, c.Normal) + pt.BaseSeparation;

					FP velocityBias = FP.Zero, massScale = FP.One, impulseScale = FP.Zero;
					if (s > FP.Zero) {
						// Speculative: not yet touching — cap approach velocity so this substep can't tunnel past the surface.
						velocityBias = s * invH;
					} else if (useBias) {
						velocityBias = FP.Max(c.Softness.MassScale * c.Softness.BiasRate * s, -contactSpeed);
						massScale = c.Softness.MassScale;
						impulseScale = c.Softness.ImpulseScale;
					}

					var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, pt.RA);
					var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, pt.RB);
					var vn = FVector3.Dot(vrB - vrA, c.Normal);

					var deltaImpulse = -pt.NormalMass * (massScale * vn + velocityBias) - impulseScale * pt.NormalImpulse;
					var newImpulse = FP.Max(pt.NormalImpulse + deltaImpulse, FP.Zero);
					deltaImpulse = newImpulse - pt.NormalImpulse;
					pt.NormalImpulse = newImpulse;
					pt.TotalNormalImpulse += newImpulse;
					totalNormalImpulse += pt.TotalNormalImpulse;

					var p = deltaImpulse * c.Normal;
					bodyA.LinearVelocity -= bodyA.InvMass * p;
					bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(pt.RA, p);
					bodyB.LinearVelocity += bodyB.InvMass * p;
					bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(pt.RB, p);

					c.SetPoint(k, pt);
				}

				// Friction only during the unbiased "relax" pass, matching box3d. Solved once per
				// manifold through the friction anchor (RA/RB averaged across points), not per point.
				if (!useBias) {
					// Rolling resistance, right before friction (matches box3d's ordering). A
					// Coulomb-limited torque driving the bodies' relative angular velocity toward
					// zero -- see ContactConstraint.RollingResistance's remarks. Skipped entirely
					// when zero, which is the default (SurfaceMaterial.RollingResistance defaults to
					// zero), so this has no effect unless a shape opts in.
					if (c.RollingResistance > FP.Zero) {
						var deltaRollingImpulse = -(c.RollingMass * (bodyB.AngularVelocity - bodyA.AngularVelocity));
						var newRollingImpulse = c.RollingImpulse + deltaRollingImpulse;

						// Box-clamp rather than box3d's precise Euclidean (disc) clamp, for the same
						// Fixed32 squaring-overflow reason as the friction cone clamp below.
						var maxRollingImpulse = FP.Abs(c.RollingResistance * totalNormalImpulse);
						newRollingImpulse = new FVector3(
							FP.Clamp(newRollingImpulse.X, -maxRollingImpulse, maxRollingImpulse),
							FP.Clamp(newRollingImpulse.Y, -maxRollingImpulse, maxRollingImpulse),
							FP.Clamp(newRollingImpulse.Z, -maxRollingImpulse, maxRollingImpulse));

						deltaRollingImpulse = newRollingImpulse - c.RollingImpulse;
						c.RollingImpulse = newRollingImpulse;

						bodyA.AngularVelocity -= bodyA.InvInertiaWorld * deltaRollingImpulse;
						bodyB.AngularVelocity += bodyB.InvInertiaWorld * deltaRollingImpulse;
					}

					var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, c.FrictionAnchorRA);
					var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, c.FrictionAnchorRB);
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
					var maxImpulse = FP.Abs(c.Friction * totalNormalImpulse);
					newX = FP.Clamp(newX, -maxImpulse, maxImpulse);
					newY = FP.Clamp(newY, -maxImpulse, maxImpulse);

					dImpulseX = newX - c.FrictionImpulseX;
					dImpulseY = newY - c.FrictionImpulseY;
					c.FrictionImpulseX = newX;
					c.FrictionImpulseY = newY;

					var p2 = dImpulseX * c.Tangent1 + dImpulseY * c.Tangent2;
					bodyA.LinearVelocity -= bodyA.InvMass * p2;
					bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(c.FrictionAnchorRA, p2);
					bodyB.LinearVelocity += bodyB.InvMass * p2;
					bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(c.FrictionAnchorRB, p2);
				}

				constraints[i] = c;
			}
		}

		private static void ApplyRestitution(List<ContactConstraint> constraints, FP restitutionThreshold) {
			for (var i = 0; i < constraints.Count; i++) {
				var c = constraints[i];

				if (c.Restitution == FP.Zero) {
					continue;
				}

				ref var bodyA = ref c.BodyA.Ref<Body>()!; // TryPrepare only stores entities with Body.
				ref var bodyB = ref c.BodyB.Ref<Body>()!;

				for (var k = 0; k < c.PointCount; k++) {
					var pt = c.GetPoint(k);

					if (pt.RelativeVelocity > -restitutionThreshold || pt.TotalNormalImpulse == FP.Zero) {
						continue;
					}

					var vrA = bodyA.LinearVelocity + FVector3.Cross(bodyA.AngularVelocity, pt.RA);
					var vrB = bodyB.LinearVelocity + FVector3.Cross(bodyB.AngularVelocity, pt.RB);
					var vn = FVector3.Dot(vrB - vrA, c.Normal);

					var impulse = -pt.NormalMass * (vn + c.Restitution * pt.RelativeVelocity);
					var newImpulse = FP.Max(pt.NormalImpulse + impulse, FP.Zero);
					impulse = newImpulse - pt.NormalImpulse;
					pt.NormalImpulse = newImpulse;
					pt.TotalNormalImpulse += impulse;

					var p = impulse * c.Normal;
					bodyA.LinearVelocity -= bodyA.InvMass * p;
					bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(pt.RA, p);
					bodyB.LinearVelocity += bodyB.InvMass * p;
					bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(pt.RB, p);

					c.SetPoint(k, pt);
				}

				constraints[i] = c;
			}
		}

		private static void FinalizeBodies(List<W.Entity> bodies) {
			List<W.Entity>? escaped = null;
			foreach (var entity in bodies) {
				// Mut, not Ref: this is the one place Transform actually changes, and
				// ShapeProxySystem's AABB-refresh pass only reacts to AllChanged<Body>.
				// bodies is built from a Query<All<Body>> filter.
				ref var body = ref entity.Mut<Body>()!;

				body.Center += body.DeltaPosition;
				body.Transform.Rotation = FQuaternion.Normalize(body.DeltaRotation * body.Transform.Rotation);
				body.Transform.Position = body.Center + -(body.Transform.Rotation * body.LocalCenter);
				if (!PhysicsValidation.IsInsideSimulationBounds(body.Transform.Position)) {
					escaped ??= new List<W.Entity>();
					escaped.Add(entity);
				}

				body.DeltaPosition = FVector3.Zero;
				body.DeltaRotation = FQuaternion.Identity;
				body.Force = FVector3.Zero;
				body.Torque = FVector3.Zero;

				// Rotation changed — refresh the world-frame inverse inertia for next tick's solve.
				var rotationMatrix = FMatrix3.FromQuaternion(body.Transform.Rotation);
				body.InvInertiaWorld = rotationMatrix * body.InvInertiaLocal * FMatrix3.Transpose(rotationMatrix);
			}

			// Escaping bodies leave the simulation instead of throwing mid-step: EscapeMargin keeps all of
			// their geometry inside the Q16.16 envelope, and disabling drops their proxies and contacts
			// before the next broad-phase update narrows anything. bodies is GID-sorted, so this is deterministic.
			if (escaped != null) {
				foreach (var entity in escaped) {
					BodyOperations.Disable(entity);
					entity.Set<OutOfPhysicsBounds>();
				}
			}
		}

		private static void StoreImpulses(List<ContactConstraint> constraints, FP hitEventThreshold) {
			foreach (var c in constraints) {
				ref var contact = ref c.ContactEntity.Ref<Contact>()!; // TryPrepare only stores entities with Contact.
				for (var k = 0; k < c.PointCount; k++) {
					var manifoldPoint = contact.Manifold.GetPoint(k);
					manifoldPoint.NormalImpulse = c.GetPoint(k).NormalImpulse;
					contact.Manifold.SetPoint(k, manifoldPoint);
				}

				contact.FrictionImpulse = c.FrictionImpulseX * c.Tangent1 + c.FrictionImpulseY * c.Tangent2;
				contact.RollingImpulse = c.RollingImpulse;

				if (!c.EnableHitEvents) {
					continue;
				}
				var bestIndex = -1;
				var bestSpeed = hitEventThreshold;
				for (var k = 0; k < c.PointCount; k++) {
					var point = c.GetPoint(k);
					var approachSpeed = -point.RelativeVelocity;
					if (approachSpeed > bestSpeed && point.TotalNormalImpulse > FP.Zero) {
						bestSpeed = approachSpeed;
						bestIndex = k;
					}
				}
				if (bestIndex >= 0) {
					if (!c.BodyA.Has<Body>() || !c.BodyB.Has<Body>()) {
						continue;
					}
					var point = c.GetPoint(bestIndex);
#pragma warning disable FFSECS0042 // Constraint bodies are admitted by TryPrepare only after resolving entities with Body; Has guards stale entities above.
					ref var bodyA = ref c.BodyA.Ref<Body>();
					ref var bodyB = ref c.BodyB.Ref<Body>();
#pragma warning restore FFSECS0042
					var pointA = bodyA.Center + bodyA.DeltaPosition + bodyA.DeltaRotation * point.RA;
					var pointB = bodyB.Center + bodyB.DeltaPosition + bodyB.DeltaRotation * point.RB;
					W.SendEvent(new ContactHitEvent {
						ShapeA = c.ShapeA,
						ShapeB = c.ShapeB,
						Point = FPos.Lerp(pointA, pointB, FP.Half),
						Normal = c.Normal,
						ApproachSpeed = bestSpeed,
						NormalImpulse = point.NormalImpulse,
					});
				}
			}
		}

		/// <summary>Pure angular impulse (no linear component, no application point) -- used by the rolling-resistance constraint.</summary>
		private static void ApplyAngularImpulse(W.Entity bodyAEntity, W.Entity bodyBEntity, FVector3 angularImpulse) {
			ref var bodyA = ref bodyAEntity.Ref<Body>()!; // Callers only pass entities with Body (WarmStart's constraint bodies).
			ref var bodyB = ref bodyBEntity.Ref<Body>()!;

			bodyA.AngularVelocity -= bodyA.InvInertiaWorld * angularImpulse;
			bodyB.AngularVelocity += bodyB.InvInertiaWorld * angularImpulse;
		}

		private static void ApplyImpulse(W.Entity bodyAEntity, W.Entity bodyBEntity, FVector3 rA, FVector3 rB, FVector3 p) {
			ref var bodyA = ref bodyAEntity.Ref<Body>()!; // Callers only pass entities with Body (WarmStart's constraint bodies).
			ref var bodyB = ref bodyBEntity.Ref<Body>()!;

			bodyA.LinearVelocity -= bodyA.InvMass * p;
			bodyA.AngularVelocity -= bodyA.InvInertiaWorld * FVector3.Cross(rA, p);
			bodyB.LinearVelocity += bodyB.InvMass * p;
			bodyB.AngularVelocity += bodyB.InvInertiaWorld * FVector3.Cross(rB, p);
		}
	}
}
