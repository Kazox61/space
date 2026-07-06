namespace Space.GameCore;

/// <summary>
/// The body simulation type. Each body is one of these three types, which determines how the
/// body behaves in the simulation.
/// </summary>
public enum BodyType {
	/// <summary>Zero mass, zero velocity, may be manually moved.</summary>
	Static = 0,

	/// <summary>Zero mass, velocity set by user, moved by solver.</summary>
	Kinematic = 1,

	/// <summary>Positive mass, velocity determined by forces, moved by solver.</summary>
	Dynamic = 2,
}

/// <summary>
/// Shape type.
/// </summary>
public enum ShapeType {
	/// <summary>A capsule is an extruded sphere.</summary>
	Capsule,

	/// <summary>A compound shape composed of up to 64K spheres, capsules, hulls, and meshes.</summary>
	Compound,

	/// <summary>A height field useful for terrain.</summary>
	Height,

	/// <summary>A convex hull.</summary>
	Hull,

	/// <summary>A triangle soup.</summary>
	Mesh,

	/// <summary>A sphere with an offset.</summary>
	Sphere,
}

/// <summary>
/// Joint type enumeration. Useful because all joint types use <see cref="JointId"/> and sometimes
/// you want to get the type of a joint.
/// </summary>
public enum JointType {
	Parallel,
	Distance,
	Filter,
	Motor,
	Prismatic,
	Revolute,
	Spherical,
	Weld,
	Wheel,
}
