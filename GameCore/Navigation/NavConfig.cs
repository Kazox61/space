namespace Space.GameCore;

/// <summary>
/// Buffer sizes and loop budgets for the navigation runtime. These change simulation output, so
/// every peer must build its navigation with the same values.
/// </summary>
public readonly struct NavConfig {
	/// <summary>A* expansion budget for one path search.</summary>
	public readonly int MaxIterations;

	/// <summary>
	/// Minimum portal buffer for the funnel. A corridor of length <c>L</c> needs <c>L + 1</c>
	/// portals; the runtime always reserves enough for a full <see cref="NavAgent"/> corridor.
	/// </summary>
	public readonly int MaxPortals;

	public NavConfig(int maxIterations, int maxPortals) {
		if (maxIterations <= 0) {
			throw new ArgumentOutOfRangeException(nameof(maxIterations), "Must be positive.");
		}
		if (maxPortals < 2) {
			throw new ArgumentOutOfRangeException(nameof(maxPortals), "Must hold at least the start and end portals.");
		}
		MaxIterations = maxIterations;
		MaxPortals = maxPortals;
	}

	public static NavConfig Default => new(maxIterations: 4096, maxPortals: 128);
}
