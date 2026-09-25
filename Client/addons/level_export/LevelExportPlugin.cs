#if TOOLS
using System;
using Godot;
using Space.Client.LevelAuthoring;

namespace Space.Client.Addons.LevelExport;

/// <summary>
/// Adds "Export Level" to the 3D editor toolbar and to Project > Tools. It exports the edited scene, as it is in
/// the editor (unsaved changes included), to the <c>.level.bytes</c> file next to the scene.
/// </summary>
[Tool]
public partial class LevelExportPlugin : EditorPlugin {
	private const string MenuName = "Export Level";

	private Button _button;

	public override void _EnterTree() {
		_button = new Button { Text = MenuName, TooltipText = "Export the edited scene to <scene>.level.bytes", Flat = true };
		_button.Pressed += ExportEditedScene;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _button);
		AddToolMenuItem(MenuName, Callable.From(ExportEditedScene));
	}

	public override void _ExitTree() {
		RemoveToolMenuItem(MenuName);
		if (_button is not null) {
			RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _button);
			_button.QueueFree();
			_button = null;
		}
	}

	private static void ExportEditedScene() {
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		if (root is null || string.IsNullOrEmpty(root.SceneFilePath)) {
			Report("Level export: open a saved map scene first.", EditorToaster.Severity.Warning);
			return;
		}

		try {
			var summary = LevelExporter.ExportToFile(root, LevelExporter.OutputPathFor(root.SceneFilePath));
			GD.Print(summary);
			Report($"Level exported: {LevelExporter.OutputPathFor(root.SceneFilePath).GetFile()}", EditorToaster.Severity.Info);
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
		} catch (Exception exception) {
			GD.PushError($"Level export failed: {exception.Message}");
			Report($"Level export failed: {exception.Message}", EditorToaster.Severity.Error);
		}
	}

	private static void Report(string message, EditorToaster.Severity severity) =>
		EditorInterface.Singleton.GetEditorToaster().PushToast(message, severity);
}
#endif
