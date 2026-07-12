using FFS.Libraries.StaticEcs;
using Fixed32;

namespace Space.GameCore;

/// <summary>
/// One contact plane collected by <c>CharacterMover.CollideMover</c>, consumed by
/// <see cref="MoverSolver"/>. Ported from box3d's <c>b3CollisionPlane</c> (mover.c), merged with its
/// companion <c>PlaneExtra</c> (point + shape id) since this port has no need for box3d's
/// struct-of-arrays split.
/// </summary>
public struct MoverPlane {
	/// <summary>
	/// Points away from the obstacle -- the direction the mover must move to increase separation.
	/// Note this is the opposite of <see cref="Manifold"/>'s A-to-B convention; see
	/// <c>CharacterMover.CollideMover</c>'s remarks for the sign flip.
	/// </summary>
	public FVector3 Normal;

	/// <summary>
	/// Signed separation at zero displacement (negative when overlapping), same convention as
	/// <c>ContactSolverSystem.ContactConstraintPoint.BaseSeparation</c>: separation after a
	/// tentative displacement <c>delta</c> is <c>Dot(Normal, delta) + BaseSeparation</c>.
	/// </summary>
	public FP BaseSeparation;

	/// <summary>Contact point, in the mover's own frame (mover is always shape A in the manifold query).</summary>
	public FVector3 Point;

	/// <summary>The candidate shape this plane came from, for the dynamic-body push step.</summary>
	public EntityGID ShapeGid;

	/// <summary>Maximum accumulated push allowed along this plane. <see cref="FP.MaxValue"/> = rigid.</summary>
	public FP PushLimit;

	/// <summary>Solver's accumulated push along this plane so far. Reset by <see cref="MoverSolver.SolvePlanes"/>.</summary>
	public FP Push;

	/// <summary>Whether <see cref="MoverSolver.ClipVector"/> should clip velocity against this plane.</summary>
	public bool ClipVelocity;
}

/// <summary>
/// Fixed-capacity buffer of <see cref="MoverPlane"/>s collected in one <c>CharacterMover.CollideMover</c>
/// call. Matches box3d's own fixed <c>m_planeCapacity = 8</c> (<c>samples/sample.h</c>'s
/// <c>CharacterMover</c>) and this codebase's own <c>ContactSolverSystem.ContactConstraint</c>
/// Point0-3 idiom for the identical "small, bounded, no heap allocation" need -- a plain value type
/// living on the stack as a <c>PlayerMoverSystem</c>-local, never touching ECS/resource state (so it
/// needs no rollback-serialization consideration at all).
/// </summary>
public struct MoverPlaneBuffer {
	public const int Capacity = 8;

	public int Count;

	public MoverPlane Plane0;
	public MoverPlane Plane1;
	public MoverPlane Plane2;
	public MoverPlane Plane3;
	public MoverPlane Plane4;
	public MoverPlane Plane5;
	public MoverPlane Plane6;
	public MoverPlane Plane7;

	public MoverPlane GetPlane(int index) {
		return index switch {
			0 => Plane0,
			1 => Plane1,
			2 => Plane2,
			3 => Plane3,
			4 => Plane4,
			5 => Plane5,
			6 => Plane6,
			_ => Plane7,
		};
	}

	public void SetPlane(int index, MoverPlane plane) {
		switch (index) {
			case 0: Plane0 = plane; break;
			case 1: Plane1 = plane; break;
			case 2: Plane2 = plane; break;
			case 3: Plane3 = plane; break;
			case 4: Plane4 = plane; break;
			case 5: Plane5 = plane; break;
			case 6: Plane6 = plane; break;
			default: Plane7 = plane; break;
		}
	}

	/// <summary>
	/// Appends a plane if there's room, silently dropping it otherwise -- matches box3d's own
	/// PlaneResultFcn: <c>for (...&amp;&amp; self-&gt;m_planeCount &lt; CharacterMover::m_planeCapacity; ...)</c>.
	/// </summary>
	public void Add(MoverPlane plane) {
		if (Count >= Capacity) {
			return;
		}

		SetPlane(Count, plane);
		Count += 1;
	}

	public void Clear() {
		Count = 0;
	}
}
