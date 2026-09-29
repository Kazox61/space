// Derived from xpTURN Klotho 0.14.1 (FPGeoPredicates.cs, snap grid), Apache-2.0.
// Modified for Space: Fixed64's 31 fractional bits.
using Fixed64;

namespace Space.NavBuilder;

/// <summary>
/// The exact-predicate grid navmesh vertices live on: 1/2^<see cref="FractionalBits"/> world units
/// in X and Z. Snapping floors by arithmetic shift, so it is deterministic and idempotent.
/// </summary>
public static class NavSnapGrid {
	public const int FractionalBits = 10;
	public const int Shift = FP.FractionalBits - FractionalBits;

	private const long FractionMask = (1L << Shift) - 1;

	/// <summary>Grid steps from the origin (floor).</summary>
	public static long Snap(FP value) {
		return value.RawValue >> Shift;
	}

	public static FP Unsnap(long snapped) {
		return FP.FromRaw(snapped << Shift);
	}

	public static FP Quantize(FP value) {
		return FP.FromRaw(value.RawValue & ~FractionMask);
	}

	public static bool IsOnGrid(FP value) {
		return (value.RawValue & FractionMask) == 0;
	}
}
