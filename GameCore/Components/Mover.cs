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
	/// Whether the last pogo ray (<see cref="CharacterMover.UpdatePogoGrounding"/>) found ground
	/// within reach, computed fresh each tick from <see cref="CapsuleCenter1"/> -- box3d's own
	/// "pogo stick" ground check (CharacterMover::SolveMove, samples/sample.cpp). Drives gravity
	/// reset and jump eligibility.
	/// </summary>
	public bool Grounded;

	/// <summary>
	/// Vertical spring-damper state for the pogo suspension, persisted across ticks so the spring
	/// integrates smoothly instead of resetting every tick. Zero while airborne.
	/// </summary>
	public FP PogoVelocity;

	/// <summary>
	/// Seconds remaining before <see cref="CharacterMover.UpdatePogoGrounding"/> is allowed to
	/// report grounded again after a jump, ported from box3d's more complete character sample
	/// (samples/sample_character.cpp's m_jumpCooldown/CategorizeGround). Without this, the pogo ray
	/// -- evaluated from the mover's *pre-movement* position every tick, before this tick's jump
	/// impulse has actually displaced anything -- immediately re-reports grounded=true on the very
	/// next tick (a single tick's displacement is rarely enough to clear the ray's range), which
	/// zeros the fresh jump velocity right back to zero via the same-tick "grounded -> zero Y
	/// velocity" reset. The simpler sample this project ports the pogo math from doesn't need this
	/// because its jump input is held-key (it keeps re-asserting jump velocity every tick instead of
	/// hitting that reset), not edge-triggered like this project's.
	/// </summary>
	public FP JumpCooldown;
}
