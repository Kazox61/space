using FFS.Libraries.StaticEcs;
using Fixed32;

namespace Space.GameCore;

/// <summary>
/// State for a <c>CharacterMover</c>-driven entity (currently just the player). The mover has no
/// <see cref="Body"/>/<see cref="Shape"/> of its own -- it queries the broad phase directly each
/// tick via <c>PlayerMoverSystem</c> -- so its capsule dimensions live here instead of on a Shape
/// component.
/// </summary>
public struct Mover : IComponent {
	public FVector3 Velocity;

	/// <summary>Local center of the capsule's first hemisphere, relative to <see cref="Transform"/>.</summary>
	public FVector3 CapsuleCenter1;

	/// <summary>Local center of the capsule's second hemisphere, relative to <see cref="Transform"/>.</summary>
	public FVector3 CapsuleCenter2;

	public FP CapsuleRadius;

	/// <summary>
	/// Whether the mover was standing on a sufficiently flat surface at the end of last tick's
	/// solve, derived from <c>CharacterMover.CollideMover</c>'s planes rather than a dedicated
	/// raycast (unlike box3d's own pogo-stick ground check) -- see <c>PlayerMoverSystem</c>'s
	/// remarks. Drives gravity reset and jump eligibility.
	/// </summary>
	public bool Grounded;
}
