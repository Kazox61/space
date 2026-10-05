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
public struct Contact : IComponent, IComponentConfig<Contact> {
	// Raw unmanaged layout changed with the SAT cache. Old peers/snapshots must not read it as v0.
	public ComponentTypeConfig<Contact> Config() => new(version: 1);
	/// <summary>
	/// Stable pair identity retained independently of the ECS links so teardown can always release
	/// the broad-phase pair, even when a relation target no longer resolves.
	/// </summary>
	public EntityGID ShapeA;
	public EntityGID ShapeB;

	public Manifold Manifold;
	/// <summary>Authoritative SAT feature history, independent of solver impulse persistence.</summary>
	public BoxSatCache SatCache;
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

public enum BoxSatAxis : byte { Invalid, FaceA, FaceB, EdgePair }

/// <summary>Result of one cache evaluation; diagnostic only, never retained in a snapshot.</summary>
public enum BoxSatResult : byte { NotBoxPair, FullSearch, SeparationHit, FaceHit, EdgeHit }

/// <summary>
/// Box3D's persistent SAT feature and original clipped-separation baseline. Inline unmanaged data
/// is serialized with Contact by StaticEcs. Geometry and ordered GIDs guard against stale features.
/// Successful hits deliberately retain the baseline (they do not accumulate separation drift).
/// </summary>
public struct BoxSatCache {
	public BoxSatAxis Axis;
	public int IndexA;
	public int IndexB;
	public FP Separation;
	public Hull GeometryA;
	public Hull GeometryB;
	public EntityGID ShapeA;
	public EntityGID ShapeB;

	internal readonly bool Matches(in Hull a, in Hull b, EntityGID shapeA, EntityGID shapeB) =>
		ShapeA == shapeA && ShapeB == shapeB
		&& GeometryA.Center.Equals(a.Center) && GeometryA.Rotation.Equals(a.Rotation) && GeometryA.HalfExtents.Equals(a.HalfExtents)
		&& GeometryB.Center.Equals(b.Center) && GeometryB.Rotation.Equals(b.Rotation) && GeometryB.HalfExtents.Equals(b.HalfExtents);
}
