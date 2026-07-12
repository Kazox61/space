using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Iterative plane-based position correction and velocity clipping for <c>CharacterMover</c>,
/// ported from box3d's mover.c (<c>b3SolvePlanes</c>/<c>b3ClipVector</c>). Pure functions -- no
/// ECS/world dependency -- operating on a caller-owned <see cref="MoverPlaneBuffer"/>.
/// </summary>
public static class MoverSolver {
	/// <summary>
	/// Finds the position delta closest to <paramref name="targetDelta"/> that satisfies every
	/// plane's non-penetration constraint, via iterative relaxation (not a true LP solve, but
	/// converges quickly for the small plane counts a mover collects).
	/// </summary>
	public static (FVector3 Delta, int Iterations) SolvePlanes(FVector3 targetDelta, ref MoverPlaneBuffer planes) {
		for (var i = 0; i < planes.Count; i++) {
			var plane = planes.GetPlane(i);
			plane.Push = FP.Zero;
			planes.SetPlane(i, plane);
		}

		var delta = targetDelta;
		var tolerance = B3Config.LinearSlop;

		var iteration = 0;
		for (; iteration < 20; iteration++) {
			var totalPush = FP.Zero;

			for (var i = 0; i < planes.Count; i++) {
				var plane = planes.GetPlane(i);

				// Add slop to prevent jitter.
				var separation = FVector3.Dot(plane.Normal, delta) + plane.BaseSeparation + B3Config.LinearSlop;
				var push = -separation;

				// Clamp accumulated push.
				var accumulatedPush = plane.Push;
				plane.Push = FP.Clamp(plane.Push + push, FP.Zero, plane.PushLimit);
				push = plane.Push - accumulatedPush;
				delta += push * plane.Normal;

				totalPush += FP.Abs(push);
				planes.SetPlane(i, plane);
			}

			if (totalPush < tolerance) {
				break;
			}
		}

		return (delta, iteration);
	}

	/// <summary>
	/// Removes the components of <paramref name="vector"/> that push into any plane where
	/// <see cref="MoverPlane.ClipVelocity"/> is set, so velocity doesn't keep accumulating into a
	/// blocked direction frame after frame.
	/// </summary>
	public static FVector3 ClipVector(FVector3 vector, in MoverPlaneBuffer planes) {
		var v = vector;

		for (var i = 0; i < planes.Count; i++) {
			var plane = planes.GetPlane(i);
			if (!plane.ClipVelocity) {
				continue;
			}

			var vn = FVector3.Dot(v, plane.Normal);
			if (vn < FP.Zero) {
				v -= vn * plane.Normal;
			}
		}

		return v;
	}
}
