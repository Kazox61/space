using System;
using System.Collections.Generic;
using Godot;
using Space.GameCore;

namespace Space.Client;

public sealed class EntityViewFactory {
	private readonly Dictionary<ViewAsset, EntityViewDefinition> _definitions = [];
	private readonly EntityViewPool _pool;

	public EntityViewFactory(EntityViewCatalog catalog, EntityViewPool pool) {
		ArgumentNullException.ThrowIfNull(catalog);
		_pool = pool;

		foreach (var definition in catalog.Entries) {
			if (definition is null || definition.Scene is null) {
				throw new InvalidOperationException("Every entity view catalog entry must have a scene.");
			}
			if (!_definitions.TryAdd(definition.Asset, definition)) {
				throw new InvalidOperationException($"The entity view catalog contains more than one {definition.Asset} entry.");
			}
		}

		foreach (var asset in Enum.GetValues<ViewAsset>()) {
			if (!_definitions.ContainsKey(asset)) {
				throw new InvalidOperationException($"The entity view catalog has no entry for {asset}.");
			}
		}
	}

	public void Prewarm() {
		foreach (var definition in _definitions.Values) {
			_pool.Prewarm(definition.Scene, definition.PrewarmCount);
		}
	}

	public EntityView Create(ViewAsset asset) {
		return _pool.Rent(_definitions[asset].Scene);
	}

	public void Destroy(EntityView view) {
		_pool.Return(view);
	}

	public void Forget(EntityView view) {
		_pool.Forget(view);
	}
}
