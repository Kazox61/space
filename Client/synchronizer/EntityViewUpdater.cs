using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public partial class EntityViewUpdater : Node {
	private sealed class ActiveView(ViewAsset asset, EntityView view) {
		public ViewAsset Asset { get; } = asset;
		public EntityView View { get; } = view;
	}

	private readonly Dictionary<EntityGID, ActiveView> _activeViews = [];
	private readonly HashSet<EntityGID> _presentEntities = [];
	private readonly List<EntityGID> _staleEntities = [];
	private EntityViewPool _pool;
	private EntityViewFactory _factory;
	private bool _initialized;

	public void Initialize(EntityViewCatalog catalog) {
		if (_initialized) {
			throw new InvalidOperationException("The entity view updater is already initialized.");
		}

		_pool = new EntityViewPool();
		_factory = new EntityViewFactory(catalog, _pool);
		_factory.Prewarm();
		ProcessPriority = 1000;
		_initialized = true;
	}

	public override void _Process(double delta) {
		if (!_initialized) {
			return;
		}

		Reconcile();
	}

	public override void _ExitTree() {
		Cleanup();
	}

	public void Cleanup() {
		if (!_initialized) {
			return;
		}

		foreach (var activeView in _activeViews.Values) {
			Release(activeView.View);
		}
		_activeViews.Clear();
		_presentEntities.Clear();
		_staleEntities.Clear();
		_pool.Dispose();
		_factory = null;
		_pool = null;
		_initialized = false;
	}

	private void Reconcile() {
		_presentEntities.Clear();
		foreach (var entity in W.Query<All<ViewId>>().Entities()) {
			var gid = entity.GID;
			var asset = entity.Read<ViewId>().Value;
			_presentEntities.Add(gid);

			if (_activeViews.TryGetValue(gid, out var activeView)) {
				if (!IsUsable(activeView.View)) {
					_factory.Forget(activeView.View);
					_activeViews.Remove(gid);
				} else if (activeView.Asset == asset) {
					continue;
				} else {
					Release(activeView.View);
					_activeViews.Remove(gid);
				}
			}

			var view = _factory.Create(asset);
			AddChild(view);
			view.AssignEntity(gid);
			_activeViews.Add(gid, new ActiveView(asset, view));
		}

		_staleEntities.Clear();
		foreach (var (gid, activeView) in _activeViews) {
			if (!_presentEntities.Contains(gid)) {
				Release(activeView.View);
				_staleEntities.Add(gid);
			}
		}
		foreach (var gid in _staleEntities) {
			_activeViews.Remove(gid);
		}

		foreach (var (gid, activeView) in _activeViews) {
			if (IsUsable(activeView.View)) {
				activeView.View.UpdateEntity(gid);
			}
		}
	}

	private void Release(EntityView view) {
		if (!IsUsable(view)) {
			_factory.Forget(view);
			return;
		}

		view.RemoveEntity();
		_factory.Destroy(view);
	}

	private static bool IsUsable(EntityView view) {
		return GodotObject.IsInstanceValid(view) && !view.IsQueuedForDeletion();
	}
}
