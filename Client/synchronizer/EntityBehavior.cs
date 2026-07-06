using FFS.Libraries.StaticEcs;
using Godot;

namespace Space;

public abstract partial class EntityBehavior : Node
{
    public abstract void OnEntityAssigned(EntityGID entityGid);
    public abstract void OnEntityRemoved(EntityGID entityGid);
    public abstract void OnEntityUpdate(EntityGID entityGid);
}
