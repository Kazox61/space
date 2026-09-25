using Fixed64;

namespace Space.GameCore;

public enum FxKind : byte {
	AttackStarted,
	ShotReleased,
	ProjectileHit,
}

/// <summary>
/// A one-shot presentation effect (sound, VFX) reported by the simulation through
/// <see cref="Core{TWorld}.FxSink"/>. Carries only what the client needs to deduplicate and place
/// the effect; nothing here flows back into the simulation. See docs/rollback-presentation-events.md.
/// </summary>
public struct FxEvent {
	public FxKind Kind;
	/// <summary>The acting player's <see cref="PlayerInfo.InputChannel"/> (the shooter for projectile effects).</summary>
	public ushort Channel;
	/// <summary>Tick that identifies this event across re-simulations: the attack tick for attacks and shots, the projectile's spawn tick for hits.</summary>
	public int KeyTick;
	public FVector3 Position;
	/// <summary>
	/// Aim direction for <see cref="FxKind.AttackStarted"/> and <see cref="FxKind.ShotReleased"/>. For
	/// <see cref="FxKind.ProjectileHit"/>, the direction the impact faces (not normalized): the hit
	/// surface's outward normal when known, otherwise back along the projectile's flight.
	/// </summary>
	public FVector3 Direction;
}

/// <summary>Receives effects from the simulation, once per simulated tick that produces them -- including re-simulated ticks.</summary>
public interface IFxSink {
	void Record(in FxEvent fx, int tick);

	/// <summary>Drops everything recorded so far; called when a full sync starts a new timeline.</summary>
	void Clear();
}
