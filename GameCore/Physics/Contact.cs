using FFS.Libraries.StaticEcs;
using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Persistent interaction between two shapes, mirroring box3d's b3Contact. Lives on its own entity
/// (carrying <c>Link&lt;ShapeA&gt;</c> / <c>Link&lt;ShapeB&gt;</c>) rather than on either shape, since
/// a contact belongs to neither shape individually. Top-level (not nested in
/// <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks.
/// </summary>
public struct Contact : IComponent {
	public Manifold Manifold;
	public bool Touching;

	/// <summary>
	/// Warm-start friction impulse (2D, along the manifold's tangent basis), persisted across ticks.
	/// Manifold-level rather than per-point in box3d — solved through the manifold centroid, which
	/// for our single-point manifolds is just the one point, so it lives here instead of on
	/// <see cref="ManifoldPoint"/>.
	/// </summary>
	public FP FrictionImpulseX;
	public FP FrictionImpulseY;
}
