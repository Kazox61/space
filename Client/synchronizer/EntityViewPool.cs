using System.Collections.Generic;
using Godot;

namespace Space.Client;

public sealed class EntityViewPool {
	private readonly Dictionary<PackedScene, Queue<EntityView>> _idle = [];
	private readonly Dictionary<EntityView, PackedScene> _source = [];
	private readonly HashSet<EntityView> _idleViews = [];

	public EntityView Rent(PackedScene scene) {
		if (_idle.TryGetValue(scene, out var queue)) {
			while (queue.Count > 0) {
				var pooledView = queue.Dequeue();
				_idleViews.Remove(pooledView);
				if (GodotObject.IsInstanceValid(pooledView) && !pooledView.IsQueuedForDeletion()) {
					return pooledView;
				}
				_source.Remove(pooledView);
			}
		}

		var view = scene.Instantiate<EntityView>();
		_source.Add(view, scene);
		return view;
	}

	public void Return(EntityView view) {
		if (!GodotObject.IsInstanceValid(view) || view.IsQueuedForDeletion()) {
			Forget(view);
			return;
		}

		view.GetParent()?.RemoveChild(view);
		if (!_source.TryGetValue(view, out var scene)) {
			view.QueueFree();
			return;
		}

		if (!_idleViews.Add(view)) {
			return;
		}

		if (!_idle.TryGetValue(scene, out var queue)) {
			queue = [];
			_idle.Add(scene, queue);
		}
		queue.Enqueue(view);
	}

	public void Prewarm(PackedScene scene, int count) {
		if (!_idle.TryGetValue(scene, out var queue)) {
			queue = [];
			_idle.Add(scene, queue);
		}

		while (queue.Count < count) {
			var view = scene.Instantiate<EntityView>();
			_source.Add(view, scene);
			_idleViews.Add(view);
			queue.Enqueue(view);
		}
	}

	public void Forget(EntityView view) {
		_idleViews.Remove(view);
		_source.Remove(view);
	}

	public void Dispose() {
		foreach (var queue in _idle.Values) {
			while (queue.Count > 0) {
				var view = queue.Dequeue();
				if (GodotObject.IsInstanceValid(view) && !view.IsQueuedForDeletion()) {
					view.QueueFree();
				}
			}
		}

		_idle.Clear();
		_idleViews.Clear();
		_source.Clear();
	}
}
