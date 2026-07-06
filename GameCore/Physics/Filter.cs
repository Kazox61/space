namespace Space.GameCore;

/// <summary>
/// Used to filter collision on shapes. Affects shape-vs-shape collision and shape-versus-query
/// collision (such as ray casts).
/// </summary>
public struct Filter {
	/// <summary>
	/// The collision category bits. Normally you would just set one bit, representing your
	/// application's object types (e.g. Static = 1, Dynamic = 2, Debris = 4, Player = 8, ...).
	/// </summary>
	public ulong CategoryBits;

	/// <summary>
	/// The collision mask bits. States the categories that this shape would accept for collision.
	/// </summary>
	public ulong MaskBits;

	/// <summary>
	/// Collision groups allow a certain group of objects to never collide (negative) or always
	/// collide (positive). A group index of zero has no effect. Non-zero group filtering always
	/// wins against the mask bits.
	/// </summary>
	public int GroupIndex;

	public static Filter Default => new() {
		CategoryBits = ulong.MaxValue,
		MaskBits = ulong.MaxValue,
		GroupIndex = 0,
	};

	/// <summary>Should two shapes with these filters collide?</summary>
	public static bool ShouldCollide(Filter filterA, Filter filterB) {
		if (filterA.GroupIndex == filterB.GroupIndex && filterA.GroupIndex != 0) {
			return filterA.GroupIndex > 0;
		}

		return (filterA.MaskBits & filterB.CategoryBits) != 0 && (filterA.CategoryBits & filterB.MaskBits) != 0;
	}
}
