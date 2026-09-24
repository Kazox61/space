using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public partial class EntityViewUpdater : Node {
	private sealed class ActiveView(ViewAsset asset, EntityView view, EntityGID gid) {
		public ViewAsset Asset { get; } = asset;
		public EntityView View { get; } = view;
		public EntityGID Gid { get; set; } = gid;
	}

	/// <summary>
	/// What a view is bound to. Usually the entity's GID, but a GID is not stable across rollbacks:
	/// restoring a snapshot bumps the version of every entity slot in a segment that was empty when
	/// the snapshot was saved, so a re-simulated projectile comes back under a new GID. Projectiles
	/// are keyed by their <see cref="ProjectileOrigin"/> instead, which re-simulation reproduces, so
	/// they keep their view (and its trail) through a rollback.
	/// </summary>
	private readonly record struct ViewKey(EntityGID Gid, ushort Channel, int SpawnTick);

	private readonly Dictionary<ViewKey, ActiveView> _activeViews = [];
	private readonly HashSet<ViewKey> _presentEntities = [];
	private readonly List<ViewKey> _staleEntities = [];
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
			var key = KeyOf(entity);
			if (!_presentEntities.Add(key)) {
				// Two entities claim the same origin; don't let them fight over one view.
				key = new ViewKey(gid, 0, 0);
				_presentEntities.Add(key);
			}

			if (_activeViews.TryGetValue(key, out var activeView)) {
				if (!IsUsable(activeView.View)) {
					_factory.Forget(activeView.View);
					_activeViews.Remove(key);
				} else if (activeView.Asset == asset) {
					if (activeView.Gid != gid) {
						activeView.Gid = gid;
						activeView.View.RebindEntity(gid);
					}
					continue;
				} else {
					Release(activeView.View);
					_activeViews.Remove(key);
				}
			}

			var view = _factory.Create(asset);
			// Place the view before it enters the tree. Otherwise it enters at a stale transform (a pooled
			// view where its last entity died, a new one at the origin), and world-space particles and
			// trails start from there before AssignEntity moves it. The updater is a plain Node, so
			// the view's local transform is its global one.
			if (entity.Has<Transform>()) {
				view.Transform = TransformViewBehavior.ToGodot(entity.Read<Transform>());
			}
			AddChild(view);
			view.AssignEntity(gid);
			_activeViews.Add(key, new ActiveView(asset, view, gid));
		}

		_staleEntities.Clear();
		foreach (var (key, activeView) in _activeViews) {
			if (!_presentEntities.Contains(key)) {
				Release(activeView.View);
				_staleEntities.Add(key);
			}
		}
		foreach (var key in _staleEntities) {
			_activeViews.Remove(key);
		}

		foreach (var activeView in _activeViews.Values) {
			if (IsUsable(activeView.View)) {
				activeView.View.UpdateEntity(activeView.Gid);
			}
		}
	}

	private static ViewKey KeyOf(W.Entity entity) {
		if (entity.Has<ProjectileOrigin>()) {
			ref readonly var origin = ref entity.Read<ProjectileOrigin>();
			return new ViewKey(default, origin.Channel, origin.SpawnTick);
		}
		return new ViewKey(entity.GID, 0, 0);
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
