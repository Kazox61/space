using FFS.Libraries.StaticEcs;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>
/// Persistent interaction between two shapes, mirroring box3d's b3Contact. Lives on its own entity
/// (carrying <c>Link&lt;ShapeA&gt;</c> / <c>Link&lt;ShapeB&gt;</c>) rather than on either shape, since
/// a contact belongs to neither shape individually. Top-level (not nested in
/// <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks.
/// </summary>
public struct Contact : IComponent {
	/// <summary>
	/// Stable pair identity retained independently of the ECS links so teardown can always release
	/// the broad-phase pair, even when a relation target no longer resolves.
	/// </summary>
	public EntityGID ShapeA;
	public EntityGID ShapeB;

	public Manifold Manifold;
	/// <summary>True only while the shapes physically overlap; speculative points do not set this.</summary>
	public bool Touching;
	public bool EnableContactEvents;
	public bool EnableSensorEvents;
	public bool IsSensorContact;
	public bool ShapeAIsSensor;
	public bool ShapeBIsSensor;
	public bool IsEventOnly;

	/// <summary>
	/// World-space warm-start friction impulse. The solver projects it into the current tangent basis,
	/// so a changing contact normal cannot reinterpret stale scalar components as a new direction.
	/// </summary>
	public FVector3 FrictionImpulse;

	/// <summary>
	/// Warm-start rolling-resistance impulse (a full angular-velocity-difference impulse, not
	/// projected onto a 2D tangent basis like friction), persisted across ticks. See
	/// <c>ContactSolverSystem</c>'s rolling-resistance block, ported from box3d's contact_solver.c.
	/// </summary>
	public FVector3 RollingImpulse;

	/// <summary>Warm-start twist-friction impulse about the contact normal.</summary>
	public FP TwistImpulse;
}
