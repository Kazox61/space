using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class EntityRecipe : Resource {
	[Export]
	public LevelEntityType EntityType { get; set; } = LevelEntityType.Crate;

	[Export]
	public Godot.Collections.Array<EntityComponent> Components { get; set; } = new();

	public EntityPlacement Build(
		string sourcePath,
		Transform3D globalTransform,
		Godot.Collections.Array<EntityComponent> overrides
	) {
		var builder = new EntityPlacementBuilder(sourcePath, EntityType, LevelMarkers.ToFixed(globalTransform, sourcePath));
		ApplyLayer(builder, Components, sourcePath, "recipe");
		ApplyLayer(builder, overrides, sourcePath, "overrides");
		return builder.Build();
	}

	/// <summary>The box shape size after overrides, for editor previews; null when neither layer has one.</summary>
	public Vector3? ResolveBoxSize(Godot.Collections.Array<EntityComponent> overrides) {
		Vector3? size = null;
		foreach (var component in Components) {
			if (component is BoxShapeComponent box) {
				size = box.Size;
			}
		}
		if (overrides is not null) {
			foreach (var component in overrides) {
				if (component is BoxShapeComponent box) {
					size = box.Size;
				}
			}
		}
		return size;
	}

	/// <summary>The view asset after overrides, for editor previews; null when neither layer has a view.</summary>
	public ViewAsset? ResolveView(Godot.Collections.Array<EntityComponent> overrides) {
		ViewAsset? view = null;
		foreach (var component in Components) {
			if (component is ViewComponent viewComponent) {
				view = viewComponent.Asset;
			}
		}
		if (overrides is not null) {
			foreach (var component in overrides) {
				if (component is ViewComponent viewComponent) {
					view = viewComponent.Asset;
				}
			}
		}
		return view;
	}

	private static void ApplyLayer(
		EntityPlacementBuilder builder,
		Godot.Collections.Array<EntityComponent> components,
		string sourcePath,
		string layerName
	) => LevelMarkers.ApplyLayer(components, static component => component.Kind, component => component.Apply(builder), sourcePath, layerName);
}
