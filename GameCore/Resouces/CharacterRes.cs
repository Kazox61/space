using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public class CharacterRes : IResource {
	public FP JumpForce = 10.ToFP();
	public FP Gravity = 24.ToFP();
	public FP MoveSpeed = 7.ToFP();

	/// <summary>Muzzle velocity for <c>Core&lt;TWorld&gt;.ShootSystem</c>'s spawned projectiles.</summary>
	public FP ProjectileSpeed = 12.ToFP();

	/// <summary>
	/// Seconds a projectile lives before <c>Core&lt;TWorld&gt;.ProjectileDespawnSystem</c> despawns it,
	/// misses included -- without this they accumulate forever (each carrying a physics body, shape,
	/// and broad-phase proxy into every rollback snapshot).
	/// </summary>
	public FP ProjectileLifetime = 2.ToFP();

	/// <summary>Pogo suspension spring frequency, matching box3d's CharacterMover::SolveMove (samples/sample.cpp) default.</summary>
	public FP PogoHertz = 4.ToFP();

	/// <summary>Pogo suspension spring damping ratio, matching box3d's CharacterMover::SolveMove default.</summary>
	public FP PogoDampingRatio = FP.FromRatio(7, 10);

	/// <summary>
	/// Seconds after a jump before the pogo ray is allowed to report grounded again, matching
	/// box3d's more complete character sample's m_jumpCooldownTime default (samples/sample_character.cpp).
	/// See <see cref="Mover.JumpCooldown"/>'s remarks for why this is needed here.
	/// </summary>
	public FP JumpCooldownTime = FP.FromRatio(2, 10);

	/// <summary>
	/// Minimum upward component (dot with +Y) a ground trace's normal needs for
	/// <c>CharacterMover.UpdatePogoGrounding</c> to accept it as standable, matching box3d's
	/// m_maxSlopeAngle=45 degrees default (samples/sample_character.cpp) -- cos(45 degrees) ~ 0.7071.
	/// </summary>
	public FP MaxSlopeNormalThreshold = FP.FromRatio(7071, 10000);
}
