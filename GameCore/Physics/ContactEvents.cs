using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Sent the tick a contact's shapes start touching. One-frame, matches box3d's b3ContactBeginTouchEvent.
/// Top-level (not nested in Core&lt;TWorld&gt;) — matches this codebase's existing event convention
/// (DeadEvent, DamageEvent); RegisterAll does not reliably auto-register IEvent types nested inside
/// an open generic like Core&lt;TWorld&gt;.
/// </summary>
public struct ContactBeginTouchEvent : IEvent {
	public EntityGID ShapeA;
	public EntityGID ShapeB;
}

/// <summary>Sent the tick a contact's shapes stop touching. One-frame, matches box3d's b3ContactEndTouchEvent.</summary>
public struct ContactEndTouchEvent : IEvent {
	public EntityGID ShapeA;
	public EntityGID ShapeB;
}
