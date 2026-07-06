using Fixed32;

namespace Space.GameCore;

/// <summary>
/// A soft (spring-damper) constraint bias, ported from box3d's b3Softness/b3MakeSoft. Converts a
/// target frequency/damping ratio into the bias-rate/mass-scale/impulse-scale triple the contact
/// solver blends into its velocity solve.
/// </summary>
public struct Softness {
	public FP BiasRate;
	public FP MassScale;
	public FP ImpulseScale;

	public static Softness Make(FP hertz, FP zeta, FP h) {
		if (hertz == FP.Zero) {
			return default;
		}

		var omega = 2 * FP.Pi * hertz;
		var a1 = 2 * zeta + h * omega;
		var a2 = h * omega * a1;
		var a3 = FP.One / (FP.One + a2);

		return new Softness {
			BiasRate = omega / a1,
			MassScale = a2 * a3,
			ImpulseScale = a3
		};
	}
}
