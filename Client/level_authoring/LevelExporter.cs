using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

public static class LevelExporter {
	public static LevelData Extract(Node root) {
		var placements = new List<EntityPlacement>();
		var staticBoxes = new List<StaticBox>();
		Collect(root, root, placements, staticBoxes);
		return new LevelData(placements, staticBoxes);
	}

	public static byte[] Export(Node root) => LevelDataCodec.Serialize(Extract(root));

	/// <summary>
	/// Exports <paramref name="root"/> to <paramref name="output"/> (a <c>res://</c> or absolute path), replacing the
	/// file atomically, and returns a one-line summary. The written bytes are decoded again as a check.
	/// </summary>
	public static string ExportToFile(Node root, string output) {
		var bytes = Export(root);
		var decoded = LevelDataCodec.Deserialize(bytes);
		var absoluteOutput = ProjectSettings.GlobalizePath(output);
		var directory = Path.GetDirectoryName(absoluteOutput);
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}
		var temporary = absoluteOutput + ".tmp";
		File.WriteAllBytes(temporary, bytes);
		File.Move(temporary, absoluteOutput, overwrite: true);
		return $"exported entities={decoded.Entities.Count} staticBoxes={decoded.StaticBoxes.Count} bytes={bytes.Length} sha256={LevelDataCodec.ContentHash(bytes)} output={output}";
	}

	/// <summary>The level file that belongs to a map scene: <c>res://maps/x.tscn</c> → <c>res://maps/x.level.bytes</c>.</summary>
	public static string OutputPathFor(string scenePath) =>
		scenePath.GetBaseName() + LevelFile.Extension;

	private static void Collect(Node root, Node node, List<EntityPlacement> placements, List<StaticBox> staticBoxes) {
		switch (node) {
			case EntitySpawn spawn:
				placements.Add(spawn.Export(SourcePath(root, spawn)));
				break;
			case LevelCollider collider:
				staticBoxes.Add(collider.Export(SourcePath(root, collider)));
				break;
			case CollisionObject3D or CollisionShape3D:
				// Refuse instead of skipping, so a collider placed the Godot way never silently goes missing.
				throw new NotSupportedException($"{SourcePath(root, node)}: Godot physics nodes are not exported; use a LevelCollider.");
		}
		foreach (Node child in node.GetChildren()) {
			Collect(root, child, placements, staticBoxes);
		}
	}

	private static string SourcePath(Node root, Node node) => root.GetPathTo(node).ToString();
}
