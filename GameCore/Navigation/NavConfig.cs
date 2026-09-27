namespace Space.GameCore;

/// <summary>
/// Buffer sizes and loop budgets for the navigation runtime. These change simulation output, so
/// every peer must build its navigation with the same values.
/// </summary>
public readonly struct NavConfig {
	/// <summary>A* expansion budget for one path search.</summary>
	public readonly int MaxIterations;

	/// <summary>
	/// Portal buffer for the funnel. A corridor of length <c>L</c> needs <c>L + 1</c> portals;
	/// longer corridors are cut at the tail and the end portal is appended after the last kept edge.
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
