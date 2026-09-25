using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>
/// A static collider in a map: a <see cref="Recipe"/> plus per-instance <see cref="Overrides"/>, the collider
/// counterpart of <see cref="EntitySpawn"/>. <see cref="LevelExporter"/> turns it into a <see cref="StaticBox"/>.
/// Use its node transform for position and rotation, and a <see cref="BoxColliderComponent"/> for size.
/// </summary>
/// <remarks>
/// It is authoring data only: in the editor it draws its box as lines, in the game it draws nothing. Add a
/// mesh as a child (or anywhere else) for what players see.
/// </remarks>
[Tool]
[GlobalClass]
public partial class LevelCollider : Node3D {
	private ColliderRecipe _recipe;
	private BoxOutline _outline;

	[Export]
	public ColliderRecipe Recipe {
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
	public Godot.Collections.Array<ColliderComponent> Overrides { get; set; } = new();

	public StaticBox Export(string sourcePath) {
		if (Recipe is null) {
			throw new InvalidDataException($"{sourcePath}: collider recipe is missing.");
		}
		return Recipe.Build(sourcePath, GlobalTransform, Overrides);
	}

	public override string[] _GetConfigurationWarnings() => Recipe is null
		? ["Collider recipe is required."]
		: [];

	public override void _Ready() {
		// Recipes and overrides can change from the inspector at any time; polling the resolved size is the
		// simplest way to follow them, and it only runs in the editor.
		SetProcess(Engine.IsEditorHint());
	}

	public override void _Process(double delta) {
		_outline ??= new BoxOutline(this);
		_outline.Update(Recipe?.ResolveBoxSize(Overrides));
	}
}
