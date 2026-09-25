using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>Reusable collider defaults; the collider counterpart of <see cref="EntityRecipe"/>.</summary>
[Tool, GlobalClass]
public partial class ColliderRecipe : Resource {
	[Export]
	public Godot.Collections.Array<ColliderComponent> Components { get; set; } = new();

	public StaticBox Build(string sourcePath, Transform3D globalTransform, Godot.Collections.Array<ColliderComponent> overrides) {
		var builder = new StaticBoxBuilder(sourcePath, LevelMarkers.ToFixed(globalTransform, sourcePath));
		ApplyLayer(builder, Components, sourcePath, "recipe");
		ApplyLayer(builder, overrides, sourcePath, "overrides");
		return builder.Build();
	}

	/// <summary>The box size after overrides, for editor previews; null when neither layer has a box.</summary>
	public Vector3? ResolveBoxSize(Godot.Collections.Array<ColliderComponent> overrides) {
		Vector3? size = null;
		foreach (var component in Components) {
			if (component is BoxColliderComponent box) {
				size = box.Size;
			}
		}
		if (overrides is not null) {
			foreach (var component in overrides) {
				if (component is BoxColliderComponent box) {
					size = box.Size;
				}
			}
		}
		return size;
	}

	private static void ApplyLayer(
		StaticBoxBuilder builder,
		Godot.Collections.Array<ColliderComponent> components,
		string sourcePath,
		string layerName
	) => LevelMarkers.ApplyLayer(components, static component => component.Kind, component => component.Apply(builder), sourcePath, layerName);
}
