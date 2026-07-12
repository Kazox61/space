using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>Marks a Body-owning entity as a projectile, so <see cref="Core{TWorld}.ProjectileHitSystem"/> can tell which side of a contact is the bullet.</summary>
public struct IsProjectile : ITag { }
