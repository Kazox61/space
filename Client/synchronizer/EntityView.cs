using FFS.Libraries.StaticEcs;
using Godot;
using Godot.Collections;

namespace Space;

[GlobalClass]
public partial class EntityView : Node3D {
	[Export] private Array<EntityBehavior> _entityBehaviours = [];

	public EntityGID EntityGid { get; protected set; }

	public void AssignEntity(EntityGID entityGid) {
		EntityGid = entityGid;

		foreach (var viewBehaviour in _entityBehaviours) {
			viewBehaviour.OnEntityAssigned(EntityGid);
		}
	}

	/// <summary>
	/// The same logical entity came back under a new GID (a rollback re-created it). Keeps every
	/// behavior's presentation state; the next <see cref="UpdateEntity"/> reads the new GID.
	/// </summary>
	public void RebindEntity(EntityGID entityGid) {
		EntityGid = entityGid;
	}

	public void RemoveEntity() {
		foreach (var viewBehaviour in _entityBehaviours) {
			viewBehaviour.OnEntityRemoved(EntityGid);
		}

		EntityGid = default;
	}

	public void UpdateEntity(EntityGID entityGid) {
		foreach (var viewBehaviour in _entityBehaviours) {
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
