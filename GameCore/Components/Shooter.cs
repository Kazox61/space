using System;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Link from a projectile entity to the player entity that fired it, for damage attribution
/// (<see cref="Core{TWorld}.ProjectileHitSystem"/>'s <see cref="DamageEvent.Source"/>). No reverse
/// <c>Links&lt;T&gt;</c> side -- nothing currently needs "all projectiles fired by this player".
/// </summary>
/// <remarks>
/// Explicit <see cref="ILinkConfig{T}"/> Guid for the same reason as <see cref="BodyOwner"/>: the
/// default Guid embeds the closed <c>TWorld</c> type name, which differs between client and server
/// worlds and breaks snapshot pool matching across that boundary.
/// </remarks>
public struct Shooter : ILinkType, ILinkConfig<Shooter> {
	public ComponentTypeConfig<World<TWorld>.Link<Shooter>> Config<TWorld>() where TWorld : struct, IWorldType =>
		new(guid: new Guid("8f1a1e2a-2b3c-4d5e-9f10-000000000005"));
}
