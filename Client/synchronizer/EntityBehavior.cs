using FFS.Libraries.StaticEcs;
using Godot;

namespace Space;

public abstract partial class EntityBehavior : Node {
	public abstract void OnEntityAssigned(EntityGID entityGid);
	/// <summary>Reset all entity-specific state before the owning view returns to the pool.</summary>
	public abstract void OnEntityRemoved(EntityGID entityGid);
	public abstract void OnEntityUpdate(EntityGID entityGid);
}
