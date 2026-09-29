using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Space.GameCore;
#if TOOLS
using Space.NavBuilder;
#endif

namespace Space.Client.LevelAuthoring;

public static class LevelExporter {
	public static LevelData Extract(Node root) {
		var placements = new List<EntityPlacement>();
		var navZones = new List<NavZoneVolume>();
		Collect(root, root, placements, navZones);
		return new LevelData(placements, navZones: navZones);
	}

	/// <summary>
	/// The level file bytes for <paramref name="root"/>, with navigation baked from its static geometry and zones. Editor
	/// builds only: the bake's dependencies are not part of exported games.
	/// </summary>
	public static byte[] Export(Node root, out string navigationSummary) {
#if TOOLS
		var level = NavMeshBaker.BakeLevel(Extract(root), NavBakeSettings.Default, out var report);
		navigationSummary = report is { } bake
			? $"navTriangles={bake.Build.OutputTriangles} recastPolygons={bake.RecastPolygons} nonManifoldEdges={bake.Build.NonManifoldEdges} navZones={level.NavZones.Count}"
			: "navigation=none (no Walkable static geometry)";
		return LevelDataCodec.Serialize(level);
#else
		throw new NotSupportedException("Level export bakes navigation and is only available in editor builds.");
#endif
	}

	/// <summary>
	/// Exports <paramref name="root"/> to <paramref name="output"/> (a <c>res://</c> or absolute path), replacing the
	/// file atomically, and returns a one-line summary. The written bytes are decoded again as a check.
	/// </summary>
	public static string ExportToFile(Node root, string output) {
		var bytes = Export(root, out var navigationSummary);
		var decoded = LevelDataCodec.Deserialize(bytes);
		var absoluteOutput = ProjectSettings.GlobalizePath(output);
		var directory = Path.GetDirectoryName(absoluteOutput);
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}
		var temporary = absoluteOutput + ".tmp";
		File.WriteAllBytes(temporary, bytes);
		File.Move(temporary, absoluteOutput, overwrite: true);
		return $"exported entities={decoded.Entities.Count} staticGeometry={decoded.NavigationSources.Count} {navigationSummary} bytes={bytes.Length} sha256={LevelDataCodec.ContentHash(bytes)} output={output}";
	}

	/// <summary>The level file that belongs to a map scene: <c>res://maps/x.tscn</c> → <c>res://maps/x.level.bytes</c>.</summary>
	public static string OutputPathFor(string scenePath) =>
		scenePath.GetBaseName() + LevelFile.Extension;

	private static void Collect(Node root, Node node, List<EntityPlacement> placements, List<NavZoneVolume> navZones) {
		switch (node) {
			case EntitySpawn spawn:
				placements.Add(spawn.Export(SourcePath(root, spawn)));
				break;
			case NavZone zone:
				navZones.Add(zone.Export(SourcePath(root, zone)));
				break;
			case CollisionObject3D or CollisionShape3D:
				// Refuse instead of skipping, so a collider placed the Godot way never silently goes missing.
				throw new NotSupportedException($"{SourcePath(root, node)}: Godot physics nodes are not exported; use an EntitySpawn with a static body.");
		}
		foreach (Node child in node.GetChildren()) {
			Collect(root, child, placements, navZones);
		}
	}

	private static string SourcePath(Node root, Node node) => root.GetPathTo(node).ToString();
}
