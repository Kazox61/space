using Godot;
using Godot.Collections;
using FFS.Libraries.StaticEcs;

namespace Space;

[GlobalClass]
public partial class EntityView : Node3D
{
    [Export] private Array<EntityBehavior> _entityBehaviours = [];

    public EntityGID EntityGid { get; protected set; }

    public void AssignEntity(EntityGID entityGid)
    {
        EntityGid = entityGid;

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            GD.PrintErr("Failed to get SceneTree.");
            return;
        }

        tree.Root.AddChild(this);

        foreach (var viewBehaviour in _entityBehaviours)
        {
            viewBehaviour.OnEntityAssigned(EntityGid);
        }
    }

    public void RemoveEntity(EntityGID entityGid)
    {
        foreach (var viewBehaviour in _entityBehaviours)
        {
            viewBehaviour.OnEntityRemoved(entityGid);
        }

        GetParent().RemoveChild(this);

        EntityGid = default;
    }

    public void UpdateEntity(EntityGID entityGid)
    {
        foreach (var viewBehaviour in _entityBehaviours)
        {
            viewBehaviour.OnEntityUpdate(entityGid);
        }
    }

#if UNITY_EDITOR
	[ContextMenu("Find Behaviours and Components")]
	public void CollectViewBehaviours()
	{
		UnityEditor.Undo.RecordObject(this, "Find behaviours");
		var behaviours = GetComponentsInChildren<EntityBehaviour>(true);

		_entityBehaviours.Clear();
		foreach (var behaviour in behaviours)
		{
			_entityBehaviours.Add(behaviour);
		}

		UnityEditor.EditorUtility.SetDirty(this);
	}
#endif
}
