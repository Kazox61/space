using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Tunable constants, ported from box3d's base.h/config.h/constants.h. Box3D bases all length
/// units on meters by default; call <see cref="SetLengthUnitsPerMeter"/> once at application
/// startup if you need different units, before creating any world.
/// </summary>
public static class B3Config {
	/// <summary>
	/// Maximum parallel workers. Used for some fixed size arrays.
	/// </summary>
	public const int MaxWorkers = 32;

	/// <summary>
	/// Maximum number of tasks queued per world step.
	/// </summary>
	public const int MaxTasks = 256;

	/// <summary>
	/// Maximum number of colors in the constraint graph. Constraints that cannot find a color
	/// are added to the overflow set which is solved single-threaded.
	/// </summary>
	public const int GraphColorCount = 24;

	/// <summary>
	/// Number of contact point buckets for counting the number of contact points per shape
	/// contact pair. Reporting only, doesn't affect simulation.
	/// </summary>
	public const int ContactManifoldCountBuckets = 8;

	/// <summary>
	/// Maximum number of simultaneous worlds that can be allocated.
	/// </summary>
	public const int MaxWorlds = 128;

	/// <summary>
	/// Maximum length of a body name.
	/// </summary>
	public const int BodyNameLength = 18;

	/// <summary>
	/// Maximum length of a shape name.
	/// </summary>
	public const int ShapeNameLength = 18;

	/// <summary>
	/// The maximum number of contact points between two touching shapes.
	/// </summary>
	public const int MaxManifoldPoints = 4;

	/// <summary>
	/// The maximum number of points to use for shape cast proxies (swept point cloud).
	/// </summary>
	public const int MaxShapeCastPoints = 64;

	/// <summary>
	/// These generous limits allow for easy hashing. See the shape pair key used by the broad phase.
	/// </summary>
	public const int ShapePower = 22;

	public const int ChildPower = 64 - 2 * ShapePower;
	public const int MaxShapes = 1 << ShapePower;
	public const int MaxChildShapes = 1 << ChildPower;

	/// <summary>
	/// The time a body must be still before it will go to sleep, in seconds.
	/// </summary>
	public static readonly FP TimeToSleep = FP.Half;

	private static FP _lengthUnitsPerMeter = FP.One;

	/// <summary>
	/// Box3D bases all length units on meters, but you may need different units for your game.
	/// This should be set at application startup and only modified once, before any world is created.
	/// </summary>
	public static void SetLengthUnitsPerMeter(FP lengthUnits) {
		_lengthUnitsPerMeter = lengthUnits;
	}

	public static FP GetLengthUnitsPerMeter() => _lengthUnitsPerMeter;

	/// <summary>
	/// A small length used as a collision and constraint tolerance. Usually chosen to be
	/// numerically significant but visually insignificant, in meters.
	/// Warning: modifying this can have a significant impact on stability.
	/// </summary>
	public static FP LinearSlop => FP.FromRatio(5, 1000) * _lengthUnitsPerMeter;

	public static FP MinCapsuleLength => LinearSlop;

	/// <summary>
	/// The distance between shapes where they are considered overlapped. Needed because GJK may
	/// return small positive values for overlapped shapes in degenerate configurations.
	/// </summary>
	public static FP OverlapSlop => FP.FromRatio(1, 10) * LinearSlop;

	/// <summary>
	/// The maximum rotation of a body per time step. This limit is very large and used to
	/// prevent numerical problems.
	/// Warning: increasing this to 0.5*Pi or greater will break continuous collision.
	/// </summary>
	public static FP MaxRotation => FP.Quarter * FP.Pi;

	/// <summary>
	/// Warning: modifying this can have a significant impact on performance and stability.
	/// </summary>
	public static FP SpeculativeDistance => 4 * LinearSlop;

	/// <summary>
	/// The rest offset used for mesh contact to reduce ghost collisions and assist with CCD.
	/// Must be at least <see cref="LinearSlop"/> and less than <see cref="SpeculativeDistance"/>.
	/// </summary>
	public static FP MeshRestOffset => LinearSlop;

	/// <summary>
	/// The default contact recycling distance.
	/// </summary>
	public static FP ContactRecycleDistance => 10 * LinearSlop;

	/// <summary>
	/// The default contact recycling world angle threshold. For performance this value is
	/// cos(angle/2)^2. This corresponds to 10 degrees.
	/// </summary>
	public static FP ContactRecycleAngularDistance => FP.FromRatio(9924039, 10000000);

	/// <summary>
	/// Used to fatten AABBs in the dynamic tree. Allows proxies to move by a small amount without
	/// triggering a tree adjustment, in meters.
	/// Warning: modifying this can have a significant impact on performance.
	/// </summary>
	public static FP MaxAabbMargin => FP.FromRatio(5, 100) * _lengthUnitsPerMeter;

	/// <summary>
	/// Per-shape AABB margin is a fraction of the shape extent (capped by <see cref="MaxAabbMargin"/>).
	/// Small shapes get small margins; large shapes are clamped to the cap.
	/// </summary>
	public static FP AabbMarginFraction => FP.FromRatio(125, 1000);
}
