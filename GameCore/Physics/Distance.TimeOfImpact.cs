using Fixed;
using Fixed32;

namespace Space.GameCore;

public static partial class Distance {
	private const int MaxTimeOfImpactIterations = 128;

	/// <summary>
	/// Computes the first time two convex proxies reach Box3D's time-of-impact target while both
	/// translate and rotate. Fractions parameterize both complete start-to-end sweeps and are limited
	/// by <see cref="TimeOfImpactInput.MaxFraction"/>. Translation is interpolated in
	/// <see cref="FPos"/> and rotation uses deterministic shortest-path
	/// <see cref="FQuaternion.Nlerp(FQuaternion, FQuaternion, FP, bool)"/>.
	/// </summary>
	/// <remarks>
	/// This uses GJK distance with conservative advancement. Its motion bound covers relative linear
	/// motion and every proxy point's NLerp rotation, so an intermediate rotational-only collision is
	/// not skipped. <see cref="TimeOfImpactState.Failed"/> means fixed-point progress or the bounded
	/// iteration budget was exhausted; its fraction, point, and normal are still the last usable
	/// conservative sample.
	/// </remarks>
	public static TimeOfImpactOutput TimeOfImpact(TimeOfImpactInput input) {
		var maxFraction = FP.Clamp01(input.MaxFraction);
		var linearSlop = B3Config.LinearSlop;
		var totalRadius = FP.Add(input.ProxyA.Radius, input.ProxyB.Radius);
		var target = FP.Max(linearSlop, totalRadius - linearSlop);
		var tolerance = FP.FromRatio(1, 4) * linearSlop;
		var motionBound = ComputeMotionBound(input);

		var fraction = FP.Zero;
		var sample = default(TimeOfImpactOutput);
		for (var iteration = 0; iteration < MaxTimeOfImpactIterations; iteration++) {
			var transformA = Interpolate(input.TransformAStart, input.TransformAEnd, input.LocalCenterA, fraction);
			var transformB = Interpolate(input.TransformBStart, input.TransformBEnd, input.LocalCenterB, fraction);
			var cache = SimplexCache.Empty;
			var distance = ShapeDistance(new DistanceInput {
				ProxyA = input.ProxyA,
				ProxyB = input.ProxyB,
				Transform = FWorldTransform.InvMul(transformA, transformB),
				UseRadii = false,
			}, ref cache);

			sample = MakeTimeOfImpactOutput(input, transformA, fraction, distance);

			if (iteration == 0 && distance.Distance <= FP.Zero) {
				sample.State = TimeOfImpactState.Overlapped;
				return sample;
			}

			if (distance.Distance <= target + tolerance) {
				sample.State = TimeOfImpactState.Hit;
				return sample;
			}

			if (fraction >= maxFraction) {
				sample.State = TimeOfImpactState.Separated;
				sample.Fraction = maxFraction;
				return sample;
			}

			if (motionBound <= FP.Zero) {
				sample.State = TimeOfImpactState.Separated;
				sample.Fraction = maxFraction;
				return sample;
			}

			// Distance between convex sets is Lipschitz-continuous under the bounded point motion.
			// Advancing by no more than this quotient cannot cross the target unnoticed.
			var step = FP.Div(distance.Distance - target - tolerance, motionBound);
			var nextFraction = FP.Min(maxFraction, FP.Add(fraction, step));
			if (nextFraction <= fraction) {
				if (distance.Distance <= target + 2 * tolerance) {
					sample.State = TimeOfImpactState.Hit;
					return sample;
				}
				sample.State = TimeOfImpactState.Failed;
				return sample;
			}

			fraction = nextFraction;
		}

		sample.State = TimeOfImpactState.Failed;
		return sample;
	}

	private static FWorldTransform Interpolate(FWorldTransform start, FWorldTransform end, FVector3 localCenter, FP fraction) {
		var rotation = FQuaternion.Nlerp(start.Rotation, end.Rotation, fraction);
		var centerStart = FWorldTransform.TransformPoint(start, localCenter);
		var centerEnd = FWorldTransform.TransformPoint(end, localCenter);
		var center = FPos.Lerp(centerStart, centerEnd, fraction);
		return new FWorldTransform(center + -(rotation * localCenter), rotation);
	}

	private static TimeOfImpactOutput MakeTimeOfImpactOutput(
		TimeOfImpactInput input,
		FWorldTransform transformA,
		FP fraction,
		DistanceOutput distance) {
		var normal = FVector3.NormalizeSafe(transformA.Rotation * distance.Normal, transformA.Rotation * FVector3.Up);
		var corePointA = FWorldTransform.TransformPoint(transformA, distance.PointA);
		var corePointB = FWorldTransform.TransformPoint(transformA, distance.PointB);
		var pointA = corePointA + input.ProxyA.Radius * normal;
		var pointB = corePointB + -input.ProxyB.Radius * normal;

		return new TimeOfImpactOutput {
			Fraction = fraction,
			Point = FPos.Lerp(pointA, pointB, FP.Half),
			Normal = normal,
		};
	}

	private static FP ComputeMotionBound(TimeOfImpactInput input) {
		var centerAStart = FWorldTransform.TransformPoint(input.TransformAStart, input.LocalCenterA);
		var centerAEnd = FWorldTransform.TransformPoint(input.TransformAEnd, input.LocalCenterA);
		var centerBStart = FWorldTransform.TransformPoint(input.TransformBStart, input.LocalCenterB);
		var centerBEnd = FWorldTransform.TransformPoint(input.TransformBEnd, input.LocalCenterB);
		var translationA = centerAEnd - centerAStart;
		var translationB = centerBEnd - centerBStart;
		var relativeTranslation = new FVector3(
			FP.Sub(translationB.X, translationA.X),
			FP.Sub(translationB.Y, translationA.Y),
			FP.Sub(translationB.Z, translationA.Z));

		var bound = L1Norm(relativeTranslation);
		bound = FP.Add(bound, RotationMotionBound(input.ProxyA, input.LocalCenterA, input.TransformAStart.Rotation, input.TransformAEnd.Rotation));
		bound = FP.Add(bound, RotationMotionBound(input.ProxyB, input.LocalCenterB, input.TransformBStart.Rotation, input.TransformBEnd.Rotation));

		// Covers 64-to-32 translation narrowing and the final fixed-point divisions.
		return FP.Add(bound, FP.CalculationsEpsilon);
	}

	private static FP RotationMotionBound(ShapeProxy proxy, FVector3 localCenter, FQuaternion start, FQuaternion end) {
		if (FQuaternion.Dot(start, end) < FP.Zero) {
			end = -end;
		}

		var delta = end - start;
		var deltaLength = FP.Sqrt(
			delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z + delta.W * delta.W);
		var radius = FP.Zero;
		for (var i = 0; i < proxy.Count; i++) {
			radius = FP.Max(radius, L1Norm(proxy.Points[i] - localCenter));
		}

		// For shortest-path NLerp, |q'(t)| <= sqrt(2) * |end-start|. A rotated point moves
		// at no more than 2|q'|r; coefficient 4 deliberately rounds that bound upward.
		return FP.Mul(FP.MulScalar(FP.Add(deltaLength, FP.CalculationsEpsilon), 4), radius);
	}

	private static FP L1Norm(FVector3 value) {
		return FP.Add(FP.Abs(value.X), FP.Add(FP.Abs(value.Y), FP.Abs(value.Z)));
	}
}
