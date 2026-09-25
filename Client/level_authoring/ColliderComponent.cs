using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>A part of a static collider, set in a <see cref="ColliderRecipe"/> or as a <see cref="LevelCollider"/> override.</summary>
[Tool]
public abstract partial class ColliderComponent : Resource {
	public abstract LevelColliderComponentKind Kind { get; }
	public abstract void Apply(StaticBoxBuilder builder);
}
