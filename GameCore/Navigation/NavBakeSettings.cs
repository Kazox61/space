using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Settings the offline navmesh bake ran with. Recorded next to the mesh so a level states how its
/// navigation was produced; nothing in the simulation reads them.
/// </summary>
/// <param name="VoxelSize">Recast cell size on XZ, in world units.</param>
/// <param name="VoxelHeight">Recast cell height on Y, in world units.</param>
/// <param name="AgentRadius">Clearance kept from walls and obstacles.</param>
/// <param name="AgentHeight">Minimum free height above a walkable surface.</param>
/// <param name="AgentMaxClimb">Largest step up or down between walkable surfaces.</param>
/// <param name="AgentMaxSlopeDegrees">Steepest walkable surface.</param>
/// <param name="RegionMinSize">Smallest isolated region kept, in voxels per side.</param>
/// <param name="RegionMergeSize">Regions smaller than this (voxels per side) merge into neighbors.</param>
/// <param name="EdgeMaxLength">Longest boundary edge before it is split, in world units.</param>
/// <param name="EdgeMaxError">Maximum contour simplification error, in voxels.</param>
/// <param name="DetailSampleDistance">Height detail sampling spacing, in voxels; below 0.9 disables sampling.</param>
/// <param name="DetailSampleMaxError">Maximum height detail error, in voxel heights.</param>
/// <param name="LookupCellSize">Cell size of the runtime triangle lookup grid, in world units.</param>
public readonly record struct NavBakeSettings(
	FP VoxelSize,
	FP VoxelHeight,
	FP AgentRadius,
	FP AgentHeight,
	FP AgentMaxClimb,
	FP AgentMaxSlopeDegrees,
	int RegionMinSize,
	int RegionMergeSize,
	FP EdgeMaxLength,
	FP EdgeMaxError,
	FP DetailSampleDistance,
	FP DetailSampleMaxError,
	FP LookupCellSize
) {
	/// <summary>
	/// Sized for the player capsule (radius 0.5, height 2) and its 45 degree standable slope.
	/// </summary>
	public static NavBakeSettings Default => new(
		VoxelSize: FP.Quarter,
		VoxelHeight: FP.FromRatio(1, 10),
		AgentRadius: FP.Half,
		AgentHeight: 2.ToFP(),
		AgentMaxClimb: FP.FromRatio(4, 10),
		AgentMaxSlopeDegrees: 45.ToFP(),
		RegionMinSize: 8,
		RegionMergeSize: 20,
		EdgeMaxLength: 12.ToFP(),
		EdgeMaxError: FP.FromRatio(13, 10),
		DetailSampleDistance: 6.ToFP(),
		DetailSampleMaxError: FP.One,
		LookupCellSize: 4.ToFP()
	);

	public void Validate() {
		RequirePositive(VoxelSize, nameof(VoxelSize));
		RequirePositive(VoxelHeight, nameof(VoxelHeight));
		RequirePositive(AgentHeight, nameof(AgentHeight));
		RequirePositive(EdgeMaxError, nameof(EdgeMaxError));
		RequirePositive(LookupCellSize, nameof(LookupCellSize));
		RequireNonNegative(AgentRadius, nameof(AgentRadius));
		RequireNonNegative(AgentMaxClimb, nameof(AgentMaxClimb));
		RequireNonNegative(EdgeMaxLength, nameof(EdgeMaxLength));
		RequireNonNegative(DetailSampleDistance, nameof(DetailSampleDistance));
		RequireNonNegative(DetailSampleMaxError, nameof(DetailSampleMaxError));
		if (AgentMaxSlopeDegrees < FP.Zero || AgentMaxSlopeDegrees >= 90.ToFP()) {
			throw new ArgumentOutOfRangeException(nameof(AgentMaxSlopeDegrees), "Must be in [0, 90).");
		}
		if (RegionMinSize < 0 || RegionMergeSize < 0) {
			throw new ArgumentOutOfRangeException(nameof(RegionMinSize), "Region sizes must not be negative.");
		}
	}

	private static void RequirePositive(FP value, string name) {
		if (value <= FP.Zero) {
			throw new ArgumentOutOfRangeException(name, "Must be positive.");
		}
	}

	private static void RequireNonNegative(FP value, string name) {
		if (value < FP.Zero) {
			throw new ArgumentOutOfRangeException(name, "Must not be negative.");
		}
	}
}
