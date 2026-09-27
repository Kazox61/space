# Level Authoring

Production tracer bullet for typed Godot entity recipes and deterministic GameCore level files.

Author reusable defaults in an `EntityRecipe` resource. Place `EntitySpawn` nodes in a map and add only per-instance values to `Overrides`. The exporter resolves both layers, validates them, converts transforms to fixed point, and writes a versioned `.level.bytes` file with an embedded payload hash.

Export from the editor: open the map scene and press **Export Level** in the 3D viewport toolbar (or Project → Tools → Export Level). It writes `<scene>.level.bytes` next to the scene from the scene as it is in the editor, unsaved changes included, and shows the result as a toast. The `Level Export` addon in `addons/level_export` must be enabled (it is in `project.godot`).

Export a map headlessly (`--output` defaults to `<scene>.level.bytes`):

```sh
/path/to/Godot --headless --path Client level_authoring/level_export_runner.tscn -- \
  --input=res://maps/level_pipeline_test.tscn \
  --output=res://maps/level_pipeline_test.level.bytes
```

Everything is set up the same way: a marker node with a `Recipe` resource for defaults and `Overrides` for per-instance values.

| Marker | Recipe | Components | Exports |
| --- | --- | --- | --- |
| `EntitySpawn` | `EntityRecipe` (e.g. `maps/crate_recipe.tres`) | Health, Loot, Body, BoxShape, View | an entity placement |
| `LevelCollider` | `ColliderRecipe` (e.g. `maps/static_box_recipe.tres`) | BoxCollider (size) | a static box |

A `NavZone` node marks a switchable navigation zone (a door, gate or bridge): set `ZoneId` (the node name when empty) and `Size`, and rotate it only about Y. The bake splits the navmesh along the box, so the simulation can close exactly the ground inside it through the zone's `NavZoneState` entity. The box must enclose the floor it switches, zones must not overlap, and a zone over no walkable ground fails the export. It draws blue lines in the editor and does not collide.

Markers take position and rotation from their node transform and must have unit scale; sizes live in components. A `LevelCollider` also declares how it contributes to the offline navigation bake: `Walkable`, `ObstacleOnly`, or `Excluded`. Every export bakes a navmesh from these colliders (`NavBakeSettings.Default`, sized for the player capsule) and stores it with its settings in the level file; a map without a `Walkable` collider gets no navmesh. The bake runs only in editor builds: `Space.NavBuilder` and DotRecast are referenced in the `Debug` configuration alone, so exported games never contain them. It draws its box as orange lines in the editor and nothing in the game, so give it a mesh (a child works) for what players see. Godot physics nodes (`StaticBody3D`, `CollisionShape3D`, `Area3D`) are refused by the exporter.

Every Godot export preset must list `*.level.bytes` under Resources → "Filters to export non-resource files", or exported builds stop at startup with "Level file ... is missing". The editor reads presets only at startup, so restart it after the filter changes in `export_presets.cfg`.

The server and client load the exported file at startup (`--level` selects another one); see "Startup integration" in `docs/level-pipeline.md`. Re-export after editing a map and commit the `.level.bytes` file.
