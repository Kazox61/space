using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using FP64 = Fixed64.FP;
using Matrix64 = Fixed64.FMatrix3;
using Vector64 = Fixed64.FVector3;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Recomputes a body's mass, center of mass, and inertia from its shapes. Ported from box3d's
	/// b3UpdateBodyMassData (body.c). Skips the minExtent/maxExtent bookkeeping box3d also does
	/// here — that only feeds sleeping/CCD, both out of scope for this pass.
	/// </summary>
	public static class BodyMassUpdate {
		public static void Update(W.Entity bodyEntity) {
			ref var body = ref bodyEntity.Ref<Body>()!; // Callers only ever pass body entities.

			// Static and kinematic bodies have zero mass; only their center tracks the transform.
			if (body.Type != BodyType.Dynamic || !bodyEntity.Has<W.Links<Shapes>>()) {
				body.Mass = FP.Zero;
				body.Inertia = FMatrix3.Zero;
				body.InvMass = FP.Zero;
				body.InvInertiaLocal = FMatrix3.Zero;
				body.InvInertiaWorld = FMatrix3.Zero;
				body.LocalCenter = FVector3.Zero;
				body.Center = body.Transform.Position;
				return;
			}

			ref var shapeLinks = ref bodyEntity.Ref<W.Links<Shapes>>();
			var count = shapeLinks.Length;
			var masses = new MassData[count];

			// Pass 1: accumulate mass and the (not yet centered) mass-weighted center.
			var totalMass = FP64.Zero;
			var weightedCenter = Vector64.Zero;
			for (var i = 0; i < count; i++) {
				if (!shapeLinks[i].Value.TryUnpack<TWorld>(out var shapeEntity)) {
					continue;
				}

				ref readonly var shape = ref shapeEntity.Read<Shape>()!; // Links<Shapes> always resolves to shape entities.
				if (shape.Density == FP.Zero) {
					continue;
				}

				var massData = shape.ComputeMass();
				PhysicsValidation.ValidateMassData(massData, nameof(shape));
				masses[i] = massData;
				totalMass += massData.Mass.To64();
				if (totalMass > PhysicsValidation.MaximumMassOrInertia.To64()) {
					throw new ArgumentOutOfRangeException(nameof(bodyEntity), "Aggregate body mass exceeds the supported fixed-point limit.");
				}
				weightedCenter += massData.Mass.To64() * massData.Center.To64();
			}

			var maximumInverse = PhysicsValidation.MaximumInverseMassOrInertia.To64();
			if (totalMass > FP64.Zero && FP64.One / totalMass > maximumInverse) {
				throw new ArgumentOutOfRangeException(nameof(bodyEntity), "Body mass is below the supported fixed-point minimum; its inverse would exceed the Q16.16 envelope.");
			}

			var mass = totalMass.To32Checked();
			var invMass = mass > FP.Zero ? FP.One / mass : FP.Zero;
			var localCenter64 = totalMass > FP64.Zero ? weightedCenter / totalMass : Vector64.Zero;
			var localCenter = To32Checked(localCenter64);

			// Pass 2: accumulate rotational inertia about the shared center of mass (parallel axis theorem).
			var inertia64 = Matrix64.Zero;
			for (var i = 0; i < count; i++) {
				var massData = masses[i];
				if (massData.Mass == FP.Zero) {
					continue;
				}

				var offset = localCenter64 - massData.Center.To64();
				inertia64 += To64(massData.Inertia) + Steiner(massData.Mass.To64(), offset);
			}

			RequireEntriesWithin(inertia64, PhysicsValidation.MaximumMassOrInertia.To64(), nameof(bodyEntity), "Aggregate body inertia exceeds the supported fixed-point limit.");
			var invInertiaLocal = FMatrix3.Zero;
			var invInertiaWorld = FMatrix3.Zero;
			// Fixed-rotation bodies discard their inertia below, so only rotating bodies need an invertible one.
			if (totalMass > FP64.Zero && !body.HasFixedRotation) {
				// Inertia floor: small shapes (r ~0.1 bullets) have principal inertia below Q16.16 resolution.
				// Raising every principal moment by the floor keeps the tensor positive definite (and
				// physical: the triangle inequality still holds), bounds the inverse by 1/floor, and lets the
				// body rotate instead of silently getting a zero or overflowing inverse.
				var floor = FP64.One / maximumInverse;
				var smallest = FP64.Min(inertia64.Cx.X, FP64.Min(inertia64.Cy.Y, inertia64.Cz.Z));
				if (smallest < floor) {
					inertia64 += floor * Matrix64.Identity;
				}

				// Invert the tensor normalized by its largest moment: the raw determinant of a small tensor
				// underflows even Fixed64, while the normalized one stays well conditioned.
				var largest = FP64.Max(inertia64.Cx.X, FP64.Max(inertia64.Cy.Y, inertia64.Cz.Z));
				var normalizedInverse = Matrix64.InvertTranspose((FP64.One / largest) * inertia64);
				if (normalizedInverse.Cx == Vector64.Zero) {
					throw new ArgumentOutOfRangeException(nameof(bodyEntity), "Body inertia tensor is degenerate.");
				}
				invInertiaLocal = To32Checked((FP64.One / largest) * normalizedInverse);
				var rotation = FMatrix3.FromQuaternion(body.Transform.Rotation);
				invInertiaWorld = rotation * invInertiaLocal * FMatrix3.Transpose(rotation);
			}

			var inertia = To32Checked(inertia64);
			var oldCenter = body.Center;
			body.Mass = mass;
			body.InvMass = invMass;
			body.Inertia = inertia;
			body.InvInertiaLocal = invInertiaLocal;
			body.InvInertiaWorld = invInertiaWorld;
			body.LocalCenter = localCenter;
			body.Center = FWorldTransform.TransformPoint(body.Transform, localCenter);

			// Center of mass moved — keep linear velocity consistent for the point that used to be the center.
			body.LinearVelocity += FVector3.Cross(body.AngularVelocity, body.Center - oldCenter);

			if (body.HasFixedRotation) {
				body.Inertia = FMatrix3.Zero;
				body.InvInertiaLocal = FMatrix3.Zero;
				body.InvInertiaWorld = FMatrix3.Zero;
			}
		}

		private static Matrix64 Steiner(FP64 mass, Vector64 origin) {
			var xx = origin.X * origin.X;
			var yy = origin.Y * origin.Y;
			var zz = origin.Z * origin.Z;
			var xy = -mass * origin.X * origin.Y;
			var xz = -mass * origin.X * origin.Z;
			var yz = -mass * origin.Y * origin.Z;
			return new Matrix64(
				new Vector64(mass * (yy + zz), xy, xz),
				new Vector64(xy, mass * (xx + zz), yz),
				new Vector64(xz, yz, mass * (xx + yy)));
		}

		private static void RequireEntriesWithin(Matrix64 value, FP64 limit, string parameterName, string message) {
			if (!Within(value.Cx, limit) || !Within(value.Cy, limit) || !Within(value.Cz, limit)) {
				throw new ArgumentOutOfRangeException(parameterName, message);
			}
		}

		private static bool Within(Vector64 value, FP64 limit) =>
			FP64.Abs(value.X) <= limit && FP64.Abs(value.Y) <= limit && FP64.Abs(value.Z) <= limit;

		private static Matrix64 To64(FMatrix3 value) => new(value.Cx.To64(), value.Cy.To64(), value.Cz.To64());

		private static FVector3 To32Checked(Vector64 value) =>
			new(value.X.To32Checked(), value.Y.To32Checked(), value.Z.To32Checked());

		private static FMatrix3 To32Checked(Matrix64 value) =>
			new(To32Checked(value.Cx), To32Checked(value.Cy), To32Checked(value.Cz));
	}
}
