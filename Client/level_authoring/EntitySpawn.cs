using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>
/// Places an entity in a map: a <see cref="Recipe"/> plus per-instance <see cref="Overrides"/>. In the editor it
/// shows the entity's view scene (looked up in the view catalog) as a preview; in the game it shows nothing.
/// </summary>
[Tool]
[GlobalClass]
public partial class EntitySpawn : Node3D {
	private const string ViewCatalogPath = "res://config/entity_view_catalog.tres";

	private EntityRecipe _recipe;
	private const string PreviewName = "ViewPreview";
	private const string PreviewAssetMeta = "view_asset";

	private ViewAsset? _warnedAsset;
	private ulong _nextAttemptMsec;
	private BoxOutline _outline;

	[Export]
	public EntityRecipe Recipe {
		get => _recipe;
		set {
			if (_recipe == value) {
				return;
			}
			_recipe = value;
			UpdateConfigurationWarnings();
		}
	}

	[Export]
	public Godot.Collections.Array<EntityComponent> Overrides { get; set; } = new();

	public EntityPlacement Export(string sourcePath) {
		if (Recipe is null) {
			throw new InvalidDataException($"{sourcePath}: entity recipe is missing.");
		}
		return Recipe.Build(sourcePath, GlobalTransform, Overrides);
	}

	public override void _Ready() {
		// Recipes and overrides can change from the inspector at any time; polling the resolved view is the
		// simplest way to follow them, and it only runs in the editor.
		SetProcess(Engine.IsEditorHint());
	}

	public override void _Process(double delta) {
		_outline ??= new BoxOutline(this);
		_outline.Update(Recipe?.ResolveBoxSize(Overrides));
		UpdatePreview(Recipe?.ResolveView(Overrides));
	}

	/// <summary>
	/// Keeps an instance of the view scene as an internal child. Its state lives on the node (name and a meta tag),
	/// not in C# fields, so it stays correct across the editor's C# reloads.
	/// </summary>
	private void UpdatePreview(ViewAsset? asset) {
		var current = FindInternalChild(this, PreviewName);
		var currentAsset = current?.HasMeta(PreviewAssetMeta) == true ? (ViewAsset?)(ViewAsset)current.GetMeta(PreviewAssetMeta).AsInt32() : null;
		if (current is not null && currentAsset == asset) {
			return;
		}
		if (current is null && (asset is null || Time.GetTicksMsec() < _nextAttemptMsec)) {
			return;
		}

		if (current is not null) {
			RemoveChild(current);
			current.QueueFree();
		}
		if (asset is not { } viewAsset) {
			return;
		}
		var scene = FindViewScene(viewAsset, warn: _warnedAsset != viewAsset);
		if (scene is null) {
			// Retry now and then (the catalog may be edited), but warn once per asset.
			_warnedAsset = viewAsset;
			_nextAttemptMsec = Time.GetTicksMsec() + 2000;
			return;
		}
		_warnedAsset = null;
		// Internal and unowned: hidden from the scene dock and never saved. The view scene's scripts are not tool
		// scripts, so nothing in it runs in the editor.
		var preview = scene.Instantiate();
		preview.Name = PreviewName;
		preview.SetMeta(PreviewAssetMeta, (int)viewAsset);
		AddChild(preview, false, InternalMode.Back);
	}

	internal static Node FindInternalChild(Node parent, string name) {
		foreach (Node child in parent.GetChildren(includeInternal: true)) {
			if (child.Name == name) {
				return child;
			}
		}
		return null;
	}

	private PackedScene FindViewScene(ViewAsset asset, bool warn) {
		// Read the catalog through Get() rather than casting to EntityViewCatalog: in the editor its C# script is a
		// placeholder (not a tool script). Load it fresh, bypassing the cache: a cached copy can be stale or lose its
		// values across the editor's C# reloads, and fresh loads also pick up catalog edits. Only runs when a
		// preview is (re)built.
		var catalog = ResourceLoader.Exists(ViewCatalogPath)
			? ResourceLoader.Load(ViewCatalogPath, "", ResourceLoader.CacheMode.Ignore)
			: null;
		if (catalog is null) {
			if (warn) {
				GD.PushWarning($"{Name}: view catalog '{ViewCatalogPath}' not found; no preview.");
			}
			return null;
		}
		foreach (var entry in catalog.Get("Entries").AsGodotArray()) {
			var definition = entry.AsGodotObject();
			if (definition is not null && definition.Get("Asset").AsInt32() == (int)asset) {
				return definition.Get("Scene").As<PackedScene>();
			}
		}
		if (warn) {
			GD.PushWarning($"{Name}: view catalog has no scene for {asset}; no preview.");
		}
		return null;
	}

	public override string[] _GetConfigurationWarnings() => Recipe is null
		? ["Entity recipe is required."]
		: [];
}
