using System;
using Fixed;
using Fixed32;
using FP64 = Fixed64.FP;
using Vector64 = Fixed64.FVector3;

namespace Space.GameCore;

internal static class PhysicsMassValidation {
	public static readonly FP MaximumMassOrInertia = 1000.ToFP();

	public static void ValidateShape(in Shape shape, BodyType bodyType, string parameterName) {
		if (bodyType == BodyType.Dynamic && shape.Density > FP.Zero) {
			ValidateMassData(shape.ComputeMass(), parameterName);
		}
	}

	public static void ValidateMassData(in MassData data, string parameterName) {
		if (data.Mass < FP.Zero || data.Mass > MaximumMassOrInertia) {
			throw new ArgumentOutOfRangeException(parameterName, $"Mass must be within [0, {MaximumMassOrInertia}].");
		}
		RequireEntry(data.Inertia.Cx.X, parameterName);
		RequireEntry(data.Inertia.Cx.Y, parameterName);
		RequireEntry(data.Inertia.Cx.Z, parameterName);
		RequireEntry(data.Inertia.Cy.X, parameterName);
		RequireEntry(data.Inertia.Cy.Y, parameterName);
		RequireEntry(data.Inertia.Cy.Z, parameterName);
		RequireEntry(data.Inertia.Cz.X, parameterName);
		RequireEntry(data.Inertia.Cz.Y, parameterName);
		RequireEntry(data.Inertia.Cz.Z, parameterName);
	}

	private static void RequireEntry(FP value, string parameterName) {
		if (value < -MaximumMassOrInertia || value > MaximumMassOrInertia) {
			throw new ArgumentOutOfRangeException(parameterName, $"Mass and inertia entries must be within +/-{MaximumMassOrInertia}.");
		}
	}
}

public abstract partial class Core<TWorld> {
	/// <summary>Enforces the deterministic Q16.16 operating envelope at public physics boundaries.</summary>
	public static class PhysicsValidation {
		public static readonly FP MaximumCoordinate = 8192.ToFP();
		public static readonly FP MaximumDynamicExtent = 4.ToFP();
		public static readonly FP MaximumStaticExtent = 40.ToFP();
		public static readonly FP MaximumDensity = 2.ToFP();
		public static readonly FP MaximumMassOrInertia = PhysicsMassValidation.MaximumMassOrInertia;
		public static readonly FP MaximumInverseMassOrInertia = 4096.ToFP();
		public static readonly FP MaximumLinearSpeed = 60.ToFP();
		/// <summary>
		/// Cap for accumulated body force and torque. At one sub-step and 60 Hz, the largest inverse mass or
		/// inertia (4096) times h * 250 is ~17000, so IntegrateVelocities' Fixed32 products stay inside Q16.16.
		/// </summary>
		public static readonly FP MaximumForceOrTorque = 250.ToFP();
		/// <summary>Hard cap for bodies with <see cref="Body.AllowFastRotation"/>, which the solver never clamps; keeps squared magnitudes inside Q16.16.</summary>
		public static readonly FP MaximumFastAngularSpeed = 100.ToFP();
		public static readonly FP MaximumQueryDistance = 100.ToFP();
		/// <summary>
		/// Cap on a query shape's radius plus twice its core bounding radius: what the largest dynamic
		/// shape (2 * sqrt(3) * <see cref="MaximumDynamicExtent"/>) can reach. GJK's Q16.16 budget (see
		/// Distance.FarDistance) covers a static shape against a dynamic one, so a query shape may be no
		/// costlier than a dynamic one. Large spheres pass (their core bound is 0); large boxes don't.
		/// </summary>
		public static readonly FP MaximumQuerySpan = FP.Sqrt(12.ToFP()) * MaximumDynamicExtent;

		/// <summary>
		/// Headroom between the creation envelope and the runtime escape line. It exceeds the reach of any
		/// valid shape (a static-extent box rotated about its body origin) plus one tick of travel, so a body
		/// or character caught at the escape line still has all of its geometry inside +/-MaximumCoordinate.
		/// </summary>
		public static readonly FP EscapeMargin = 128.ToFP();
		public static readonly FP EscapeCoordinate = MaximumCoordinate - EscapeMargin;

		public static void ValidateWorld(PhysicsWorld world) {
			if (world.SubStepCount < 1 || world.SubStepCount > 16)
				throw new ArgumentOutOfRangeException(nameof(world.SubStepCount), "Physics sub-step count must be in [1, 16].");
			RequireRange(world.ContactHertz, FP.CalculationsEpsilon, 60.ToFP(), nameof(world.ContactHertz));
			RequireRange(world.ContactDampingRatio, FP.Zero, 20.ToFP(), nameof(world.ContactDampingRatio));
			RequireRange(world.ContactSpeed, FP.Zero, MaximumLinearSpeed, nameof(world.ContactSpeed));
			RequireRange(world.RestitutionThreshold, FP.Zero, MaximumLinearSpeed, nameof(world.RestitutionThreshold));
			RequireRange(world.HitEventThreshold, FP.Zero, MaximumLinearSpeed, nameof(world.HitEventThreshold));
			RequireRange(world.MaximumLinearSpeed, FP.CalculationsEpsilon, MaximumLinearSpeed, nameof(world.MaximumLinearSpeed));
			ValidateMagnitude(world.Gravity, MaximumLinearSpeed, nameof(world.Gravity));
		}

		public static void ValidateTransform(FWorldTransform transform, string parameterName) {
			ValidatePosition(transform.Position, parameterName);
			if (FQuaternion.LengthSqr(transform.Rotation) <= FP.CalculationsEpsilonSqr)
				throw new ArgumentException("Physics rotations must be non-zero quaternions.", parameterName);
		}

		public static void ValidatePosition(FPos position, string parameterName) {
			var limit = MaximumCoordinate.To64();
			if (position.X < -limit || position.X > limit
				|| position.Y < -limit || position.Y > limit
				|| position.Z < -limit || position.Z > limit) {
				throw new ArgumentOutOfRangeException(parameterName, $"Physics coordinates must be within +/-{MaximumCoordinate}.");
			}
		}

		/// <summary>True while a moving body or character origin is inside the runtime escape line.</summary>
		public static bool IsInsideSimulationBounds(FPos position) {
			var limit = EscapeCoordinate.To64();
			return position.X >= -limit && position.X <= limit
				&& position.Y >= -limit && position.Y <= limit
				&& position.Z >= -limit && position.Z <= limit;
		}

		public static void ValidateShape(in Shape shape, BodyType bodyType, FWorldTransform bodyTransform, string parameterName) {
			var extentLimit = bodyType == BodyType.Dynamic ? MaximumDynamicExtent : MaximumStaticExtent;
			switch (shape.Type) {
				case ShapeType.Sphere:
					RequirePositive(shape.SphereShape.Radius, parameterName, "Sphere radius");
					ValidateLocalBounds(shape.SphereShape.Center, shape.SphereShape.Radius, extentLimit, parameterName);
					break;
				case ShapeType.Capsule:
					RequirePositive(shape.CapsuleShape.Radius, parameterName, "Capsule radius");
					ValidateLocalBounds(shape.CapsuleShape.Center1, shape.CapsuleShape.Radius, extentLimit, parameterName);
					ValidateLocalBounds(shape.CapsuleShape.Center2, shape.CapsuleShape.Radius, extentLimit, parameterName);
					break;
				case ShapeType.Hull:
					RequirePositive(shape.HullShape.HalfExtents.X, parameterName, "Box half-extent X");
					RequirePositive(shape.HullShape.HalfExtents.Y, parameterName, "Box half-extent Y");
					RequirePositive(shape.HullShape.HalfExtents.Z, parameterName, "Box half-extent Z");
					if (FQuaternion.LengthSqr(shape.HullShape.Rotation) <= FP.CalculationsEpsilonSqr)
						throw new ArgumentException("Box rotations must be non-zero quaternions.", parameterName);
					var hullBounds = shape.ComputeAABB(FTransform.Identity);
					ValidateBounds(hullBounds, extentLimit, parameterName);
					break;
				default:
					throw new NotSupportedException($"Shape type {shape.Type} is not supported by the physics engine.");
			}

			RequireRange(shape.Density, FP.Zero, MaximumDensity, nameof(shape.Density));
			RequireRange(shape.Material.Friction, FP.Zero, FP.One, nameof(shape.Material.Friction));
			RequireRange(shape.Material.Restitution, FP.Zero, FP.One, nameof(shape.Material.Restitution));
			RequireRange(shape.Material.RollingResistance, FP.Zero, FP.One, nameof(shape.Material.RollingResistance));
			ValidateMagnitude(shape.Material.TangentVelocity, MaximumLinearSpeed, nameof(shape.Material.TangentVelocity));

			var rotationOnly = new FTransform(FVector3.Zero, bodyTransform.Rotation);
			ValidateWorldBounds(bodyTransform.Position, shape.ComputeAABB(rotationOnly), parameterName);
			PhysicsMassValidation.ValidateShape(shape, bodyType, parameterName);
		}

		public static void ValidateMassData(in MassData data, string parameterName) =>
			PhysicsMassValidation.ValidateMassData(data, parameterName);

		/// <summary>
		/// Clamps a velocity to <paramref name="maximum"/> without overflowing its squared length. Components
		/// are first scaled so the largest equals the maximum, which keeps LengthSqr inside the numeric range.
		/// </summary>
		public static FVector3 ClampMagnitude(Vector64 value, FP maximum) {
			var limit = maximum.To64();
			var largest = FP64.Max(FP64.Abs(value.X), FP64.Max(FP64.Abs(value.Y), FP64.Abs(value.Z)));
			if (largest > limit) {
				value *= limit / largest;
			}
			if (Vector64.LengthSqr(value) > limit * limit) {
				value *= limit / Vector64.Length(value);
			}
			return new FVector3(value.X.To32Checked(), value.Y.To32Checked(), value.Z.To32Checked());
		}

		public static void ValidateQuery(FWorldTransform transform, ShapeProxy proxy, FVector3 translation) {
			ValidateTransform(transform, nameof(transform));
			if (proxy.Count <= 0 || proxy.Count > B3Config.MaxShapeCastPoints)
				throw new ArgumentException($"A shape query proxy must contain 1 to {B3Config.MaxShapeCastPoints} points.", nameof(proxy));
			if (proxy.Radius < FP.Zero || proxy.Radius > MaximumStaticExtent)
				throw new ArgumentOutOfRangeException(nameof(proxy), "Shape query radius is outside the supported range.");
			var first = transform.Rotation * proxy.Points[0];
			var lower = first;
			var upper = first;
			for (var i = 0; i < proxy.Count; i++) {
				ValidateLocalBounds(proxy.Points[i], proxy.Radius, MaximumStaticExtent, nameof(proxy));
				var point = transform.Rotation * proxy.Points[i];
				lower = FVector3.MinComponents(lower, point);
				upper = FVector3.MaxComponents(upper, point);
			}
			Distance.ProxyBoundingSphere(proxy, FMatrix3.Identity, FVector3.Zero, out var coreBound);
			ValidateQuerySpan(proxy.Radius + 2 * coreBound, nameof(proxy));
			var radius = new FVector3(proxy.Radius, proxy.Radius, proxy.Radius);
			ValidateQueryBounds(transform.Position, new FAABB(lower - radius, upper + radius), translation);
		}

		public static void ValidateCapsuleQuery(FWorldTransform transform, Capsule capsule, FVector3 translation) {
			ValidateTransform(transform, nameof(transform));
			if (capsule.Radius < FP.Zero || capsule.Radius > MaximumStaticExtent)
				throw new ArgumentOutOfRangeException(nameof(capsule), "Shape query radius is outside the supported range.");
			ValidateLocalBounds(capsule.Center1, capsule.Radius, MaximumStaticExtent, nameof(capsule));
			ValidateLocalBounds(capsule.Center2, capsule.Radius, MaximumStaticExtent, nameof(capsule));
			ValidateQuerySpan(capsule.Radius + FVector3.Distance(capsule.Center1, capsule.Center2), nameof(capsule));
			var localBounds = Capsule.ComputeAABB(capsule, new FTransform(FVector3.Zero, transform.Rotation));
			ValidateQueryBounds(transform.Position, localBounds, translation);
		}

		public static void ValidateRayQuery(FPos origin, FVector3 translation) {
			ValidatePosition(origin, nameof(origin));
			ValidateQueryBounds(origin, new FAABB(FVector3.Zero, FVector3.Zero), translation);
		}

		private static void ValidateQuerySpan(FP span, string parameterName) {
			if (span > MaximumQuerySpan)
				throw new ArgumentOutOfRangeException(parameterName, $"Shape query radius plus core diameter must be at most {MaximumQuerySpan}.");
		}

		private static void ValidateQueryBounds(FPos position, FAABB localBounds, FVector3 translation) {
			ValidateMagnitude(translation, MaximumQueryDistance, nameof(translation));
			ValidateWorldBounds(position, localBounds, nameof(position));
			ValidateWorldBounds(position + translation, localBounds, nameof(translation));
		}

		private static void ValidateLocalBounds(FVector3 center, FP radius, FP limit, string parameterName) {
			if (AbsRaw(center.X) + radius.RawValue > limit.RawValue
				|| AbsRaw(center.Y) + radius.RawValue > limit.RawValue
				|| AbsRaw(center.Z) + radius.RawValue > limit.RawValue) {
				throw new ArgumentOutOfRangeException(parameterName, $"Shape geometry must fit within +/-{limit} in body-local space.");
			}
		}

		private static void ValidateBounds(FAABB bounds, FP limit, string parameterName) {
			if (bounds.LowerBound.X < -limit || bounds.UpperBound.X > limit
				|| bounds.LowerBound.Y < -limit || bounds.UpperBound.Y > limit
				|| bounds.LowerBound.Z < -limit || bounds.UpperBound.Z > limit) {
				throw new ArgumentOutOfRangeException(parameterName, $"Shape geometry must fit within +/-{limit} in body-local space.");
			}
		}

		private static void ValidateWorldBounds(FPos position, FAABB localBounds, string parameterName) {
			var limit = MaximumCoordinate.To64();
			if (position.X + localBounds.LowerBound.X.To64() < -limit || position.X + localBounds.UpperBound.X.To64() > limit
				|| position.Y + localBounds.LowerBound.Y.To64() < -limit || position.Y + localBounds.UpperBound.Y.To64() > limit
				|| position.Z + localBounds.LowerBound.Z.To64() < -limit || position.Z + localBounds.UpperBound.Z.To64() > limit) {
				throw new ArgumentOutOfRangeException(parameterName, $"Transformed shape bounds must remain within +/-{MaximumCoordinate}.");
			}
		}

		private static void ValidateMagnitude(FVector3 value, FP limit, string parameterName) {
			if (AbsRaw(value.X) > limit.RawValue || AbsRaw(value.Y) > limit.RawValue || AbsRaw(value.Z) > limit.RawValue
				|| FVector3.LengthSqr(value) > limit * limit) {
				throw new ArgumentOutOfRangeException(parameterName, $"Vector magnitude must not exceed {limit}.");
			}
		}

		private static long AbsRaw(FP value) => Math.Abs((long)value.RawValue);

		private static void RequirePositive(FP value, string parameterName, string label) {
			if (value <= FP.Zero)
				throw new ArgumentOutOfRangeException(parameterName, $"{label} must be positive.");
		}

		private static void RequireRange(FP value, FP minimum, FP maximum, string parameterName) {
			if (value < minimum || value > maximum)
				throw new ArgumentOutOfRangeException(parameterName, $"Value must be in [{minimum}, {maximum}].");
		}
	}
}
