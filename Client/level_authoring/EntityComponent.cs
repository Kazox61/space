using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool]
public abstract partial class EntityComponent : Resource {
	public abstract LevelEntityComponentKind Kind { get; }
	public abstract void Apply(EntityPlacementBuilder builder);
}
